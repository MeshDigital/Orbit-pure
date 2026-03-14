using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO; // Added for Path
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services;

public class DatabaseService
{
    private readonly ILogger<DatabaseService> _logger;
    private readonly SchemaMigratorService _schemaMigrator;
    private readonly Repositories.ITrackRepository _trackRepository;
    private readonly Services.IO.IFileWriteService _fileWriteService;
    
    // Semaphore to serialize database write operations and prevent SQLite locking issues
    private static readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

    public DatabaseService(
        ILogger<DatabaseService> logger, 
        SchemaMigratorService schemaMigrator,
        Repositories.ITrackRepository trackRepository,
        Services.IO.IFileWriteService fileWriteService)
    {
        _logger = logger;
        _schemaMigrator = schemaMigrator;
        _trackRepository = trackRepository;
        _fileWriteService = fileWriteService;
    }

    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _currentTransaction;
    private AppDbContext? _transactionContext;

    public async Task BeginTransactionAsync()
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            _transactionContext = new AppDbContext();
            _currentTransaction = await _transactionContext.Database.BeginTransactionAsync();
        }
        catch (Exception ex)
        {
            _writeSemaphore.Release();
            _logger.LogError(ex, "Failed to begin database transaction");
            throw;
        }
    }

    public async Task CommitTransactionAsync()
    {
        if (_currentTransaction == null || _transactionContext == null)
            throw new InvalidOperationException("No active transaction to commit");

        try
        {
            await _transactionContext.SaveChangesAsync();
            await _currentTransaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to commit database transaction");
            throw;
        }
        finally
        {
            await CleanupTransactionAsync();
            _writeSemaphore.Release();
        }
    }

    public async Task RollbackTransactionAsync()
    {
        if (_currentTransaction == null) return;

        try
        {
            await _currentTransaction.RollbackAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rollback database transaction");
        }
        finally
        {
            await CleanupTransactionAsync();
            _writeSemaphore.Release();
        }
    }

    private async Task CleanupTransactionAsync()
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
        if (_transactionContext != null)
        {
            await _transactionContext.DisposeAsync();
            _transactionContext = null;
        }
    }

    public async Task RunInTransactionAsync(Func<Task> action)
    {
        await BeginTransactionAsync();
        try
        {
            await action();
            await CommitTransactionAsync();
        }
        catch
        {
            await RollbackTransactionAsync();
            throw;
        }
    }

    public async Task InitAsync()
    {
        await _schemaMigrator.InitializeDatabaseAsync();
    }

    // ===== PendingOrchestration Methods =====

    public async Task AddPendingOrchestrationAsync(string globalId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var sql = "INSERT OR IGNORE INTO PendingOrchestrations (GlobalId, AddedAt) VALUES (@id, @now)";
            await context.Database.ExecuteSqlRawAsync(sql, 
                new SqliteParameter("@id", globalId),
                new SqliteParameter("@now", DateTime.UtcNow.ToString("o")));
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task RemovePendingOrchestrationAsync(string globalId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var sql = "DELETE FROM PendingOrchestrations WHERE GlobalId = @id";
            await context.Database.ExecuteSqlRawAsync(sql, new SqliteParameter("@id", globalId));
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<string>> GetPendingOrchestrationsAsync()
    {
        using var context = new AppDbContext();
        var ids = new List<string>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GlobalId FROM PendingOrchestrations";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }

    // ===== Track Methods =====

    public async Task<List<TrackEntity>> LoadTracksAsync()
    {
        return await _trackRepository.LoadTracksAsync();
    }

    public async Task<TrackEntity?> FindTrackAsync(string globalId)
    {
        return await _trackRepository.FindTrackAsync(globalId);
    }

    public async Task SaveTrackAsync(TrackEntity track)
    {
        await _trackRepository.SaveTrackAsync(track);
    }
    
    public async Task UpdateTrackFilePathAsync(string globalId, string filePath)
    {
        await _trackRepository.UpdateTrackFilePathAsync(globalId, filePath);
    }

    public async Task RemoveTrackAsync(string globalId)
    {
        await _trackRepository.RemoveTrackAsync(globalId);
    }

    // Helper to bulk save if needed
    public async Task SaveAllAsync(IEnumerable<TrackEntity> tracks)
    {
        using var context = new AppDbContext();
        foreach(var t in tracks)
        {
            if (!await context.Tracks.AnyAsync(x => x.GlobalId == t.GlobalId))
            {
                await context.Tracks.AddAsync(t);
            }
        }
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Updates the status of a track across all playlists that contain it,
    /// then recalculates the progress counts for those playlists.
    /// </summary>
    public async Task<List<Guid>> UpdatePlaylistTrackStatusAndRecalculateJobsAsync(string trackUniqueHash, TrackStatus newStatus, string? resolvedPath, int searchRetryCount = 0, int notFoundRestartCount = 0)
    {
        return await _trackRepository.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(trackUniqueHash, newStatus, resolvedPath, searchRetryCount, notFoundRestartCount);
    }

    // ===== LibraryEntry Methods =====

    public async Task<LibraryEntryEntity?> FindLibraryEntryAsync(string uniqueHash)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Include(e => e.AudioFeatures)
            .FirstOrDefaultAsync(e => e.UniqueHash == uniqueHash);
    }



    public async Task SaveLibraryEntryAsync(LibraryEntryEntity entry)
    {
        using var context = new AppDbContext();
        
        // Robust Upsert Pattern: Check existence first to avoid DbUpdateConcurrencyException
        var existing = await context.LibraryEntries.FindAsync(entry.UniqueHash);
        
        if (existing == null)
        {
            // It doesn't exist, so we ADD it.
            context.LibraryEntries.Add(entry);
        }
        else
        {
            // It exists, so we UPDATE it.
            // Since 'existing' is tracked and 'entry' is detached, we use SetValues.
            context.Entry(existing).CurrentValues.SetValues(entry);
            
            // Ensure LastUsedAt is updated on the tracked entity
            existing.LastUsedAt = DateTime.UtcNow; 
        }
        
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Finds an enriched track by artist and title.
    /// Used by SpotifyEnrichmentService for cache-first lookups.
    /// </summary>
    public async Task<LibraryEntryEntity?> FindEnrichedTrackAsync(string artist, string title)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Where(e => e.Artist.ToLower() == artist.ToLower() && 
                       e.Title.ToLower() == title.ToLower() &&
                       e.IsEnriched)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Gets a library entry by its Guid Id.
    /// </summary>
    public async Task<LibraryEntryEntity?> GetLibraryEntryAsync(Guid id)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Include(e => e.AudioFeatures)
            .FirstOrDefaultAsync(e => e.Id == id);
    }

    /// <summary>
    /// Gets a library entry by its unique hash.
    /// </summary>
    public async Task<LibraryEntryEntity?> GetLibraryEntryAsync(string uniqueHash)
    {
        return await FindLibraryEntryAsync(uniqueHash);
    }

    /// <summary>
    /// Finds an enriched track in the global Tracks table by its GlobalId.
    /// Used by enrichment services for cache lookups.
    /// </summary>
    public async Task<TrackEntity?> FindEnrichedTrackAsync(string globalId)
    {
        return await _trackRepository.FindTrackAsync(globalId);
    }

    /// <summary>
    /// Searches library entries and returns enrichment status.
    /// Used by Mission Control for status lookups.
    /// </summary>
    public async Task<List<LibraryEntryEntity>> SearchLibraryEntriesWithStatusAsync(string query, int limit = 50)
    {
        using var context = new AppDbContext();
        var lowerQuery = query.ToLower();
        return await context.LibraryEntries
            .Where(e => e.Artist.ToLower().Contains(lowerQuery) || e.Title.ToLower().Contains(lowerQuery))
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }


    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingEnrichmentAsync(int limit)
    {
        return await _trackRepository.GetLibraryEntriesNeedingEnrichmentAsync(limit);
    }

    public async Task UpdateLibraryEntryEnrichmentAsync(string uniqueHash, TrackEnrichmentResult result)
    {
        await _trackRepository.UpdateLibraryEntryEnrichmentAsync(uniqueHash, result);
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingEnrichmentAsync(int limit)
    {
        return await _trackRepository.GetPlaylistTracksNeedingEnrichmentAsync(limit);
    }

    public async Task UpdatePlaylistTrackEnrichmentAsync(Guid id, TrackEnrichmentResult result)
    {
        await _trackRepository.UpdatePlaylistTrackEnrichmentAsync(id, result);
    }



    // ===== PlaylistJob Methods =====

    public async Task<List<PlaylistJobEntity>> LoadAllPlaylistJobsAsync()
    {
        using var context = new AppDbContext();
        return await context.Projects
            .AsNoTracking()
            .Where(j => !j.IsDeleted)
            .Include(j => j.Tracks)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();
    }

    public async Task<PlaylistJobEntity?> LoadPlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        // BUGFIX: Also exclude soft-deleted jobs, otherwise duplicate detection finds deleted jobs
        // that won't show in the library list (LoadAllPlaylistJobsAsync filters by !IsDeleted)
        return await context.Projects.AsNoTracking()
            .Include(j => j.Tracks)
            .FirstOrDefaultAsync(j => j.Id == jobId && !j.IsDeleted);

    }

    public async Task SavePlaylistJobAsync(PlaylistJobEntity job)
    {
        using var context = new AppDbContext();

        // Use the same atomic upsert pattern for PlaylistJobs.
        // EF Core will handle INSERT vs. UPDATE based on the job.Id primary key.
        // We set CreatedAt here if it's a new entity. The DB context tracks the entity state.
        if (context.Entry(job).State == EntityState.Detached)
             job.CreatedAt = DateTime.UtcNow;
        context.Projects.Update(job);
        await context.SaveChangesAsync();
        _logger.LogInformation("Saved PlaylistJob: {Title} ({Id})", job.SourceTitle, job.Id);
    }

    public async Task DeletePlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.Projects.FindAsync(jobId);
        if (job != null)
        {
            context.Projects.Remove(job);
            await context.SaveChangesAsync();
            _logger.LogInformation("Deleted PlaylistJob: {Id}", jobId);
        }
    }

    public async Task SoftDeletePlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.Projects.FindAsync(jobId);
        if (job != null)
        {
            job.IsDeleted = true;
            job.DeletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            _logger.LogInformation("Soft-deleted PlaylistJob: {Id}", jobId);
        }
    }

    public async Task<List<PlaylistJobEntity>> LoadDeletedPlaylistJobsAsync()
    {
        using var context = new AppDbContext();
        return await context.Projects
            .AsNoTracking()
            .IgnoreQueryFilters() // Must ignore filter to see deleted items
            .Where(j => j.IsDeleted)
            .Include(j => j.Tracks)
            .OrderByDescending(j => j.DeletedAt)
            .ToListAsync();
    }

    public async Task RestorePlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.Projects.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == jobId);
        if (job != null)
        {
            job.IsDeleted = false;
            job.DeletedAt = null;
            await context.SaveChangesAsync();
            _logger.LogInformation("Restored PlaylistJob: {Id}", jobId);
        }
    }


    // ===== PlaylistTrack Methods =====

    public async Task UpdateLikeStatusAsync(string trackHash, bool isLiked)
    {
        await _trackRepository.UpdateLikeStatusAsync(trackHash, isLiked);
    }

    public async Task UpdateRatingAsync(string trackHash, int rating)
    {
        await _trackRepository.UpdateRatingAsync(trackHash, rating);
    }

    public async Task<List<PlaylistTrackEntity>> LoadPlaylistTracksAsync(Guid jobId)
    {
        return await _trackRepository.LoadPlaylistTracksAsync(jobId);
    }

    public async Task<int> GetPlaylistTrackCountAsync(Guid playlistId, string? filter = null, bool? downloadedOnly = null)
    {
        return await _trackRepository.GetPlaylistTrackCountAsync(playlistId, filter, downloadedOnly);
    }

    public async Task<List<PlaylistTrackEntity>> GetPagedPlaylistTracksAsync(Guid playlistId, int skip, int take, string? filter = null, bool? downloadedOnly = null)
    {
        return await _trackRepository.GetPagedPlaylistTracksAsync(playlistId, skip, take, filter, downloadedOnly);
    }

    public async Task<List<TrackPhraseEntity>> GetPhrasesByHashAsync(string trackHash)
    {
        return await _trackRepository.GetPhrasesByHashAsync(trackHash);
    }

    public async Task SavePhrasesAsync(List<TrackPhraseEntity> phrases)
    {
        await _trackRepository.SavePhrasesAsync(phrases);
    }

    public async Task<int> GetTotalLibraryTrackCountAsync(string? filter = null, bool? downloadedOnly = null)
    {
        return await _trackRepository.GetTotalLibraryTrackCountAsync(filter, downloadedOnly);
    }

    public async Task<List<PlaylistTrackEntity>> GetPagedAllTracksAsync(int skip, int take, string? filter = null, bool? downloadedOnly = null)
    {
        return await _trackRepository.GetPagedAllTracksAsync(skip, take, filter, downloadedOnly);
    }

    public async Task<PlaylistTrackEntity?> GetPlaylistTrackByHashAsync(Guid jobId, string trackHash)
    {
        return await _trackRepository.GetPlaylistTrackByHashAsync(jobId, trackHash);
    }

    public async Task SavePlaylistTrackAsync(PlaylistTrackEntity track)
    {
        await _trackRepository.SavePlaylistTrackAsync(track);
    }

    public async Task UpdatePlaylistTrackUserPausedAsync(Guid id, bool isUserPaused)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE PlaylistTracks SET IsUserPaused = {isUserPaused} WHERE Id = {id}");
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// Saves or updates an AudioFeatures entity (Essentia analysis results).
    /// </summary>
    public async Task SaveAudioFeaturesAsync(AudioFeaturesEntity features)
    {
        var context = _transactionContext ?? new AppDbContext();
        try
        {
            if (_transactionContext == null) await _writeSemaphore.WaitAsync();

            try
            {
                var existing = await context.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == features.TrackUniqueHash);
                if (existing != null)
                {
                    context.AudioFeatures.Remove(existing);
                }
                context.AudioFeatures.Add(features);

                if (_transactionContext == null) await context.SaveChangesAsync();
            }
            finally
            {
                if (_transactionContext == null) _writeSemaphore.Release();
            }
        }
        finally
        {
            if (_transactionContext == null) await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Gets existing TechnicalDetails or creates a new one for the given PlaylistTrack.
    /// </summary>
    public async Task<TrackTechnicalEntity> GetOrCreateTechnicalDetailsAsync(Guid playlistTrackId)
    {
        return await _trackRepository.GetOrCreateTechnicalDetailsAsync(playlistTrackId);
    }

    private static async Task UpdatePlaylistJobCountersAsync(AppDbContext context, Guid playlistId)
    {
        var job = await context.Projects.FirstOrDefaultAsync(j => j.Id == playlistId);
        if (job == null)
        {
            return;
        } 
        var statuses = await context.PlaylistTracks.AsNoTracking()
            .Where(t => t.PlaylistId == playlistId)
            .Select(t => t.Status)
            .ToListAsync();

        job.TotalTracks = statuses.Count;
        job.SuccessfulCount = statuses.Count(s => s == TrackStatus.Downloaded);
        job.FailedCount = statuses.Count(s => s == TrackStatus.Failed || s == TrackStatus.Skipped);

        var remaining = statuses.Count(s => s == TrackStatus.Missing);
        if (job.TotalTracks > 0 && remaining == 0)
        {
            job.CompletedAt ??= DateTime.UtcNow;
        }
        else
        {
            job.CompletedAt = null;
        }
    }

    public async Task SavePlaylistTracksAsync(IEnumerable<PlaylistTrackEntity> tracks)
    {
        await _trackRepository.SavePlaylistTracksAsync(tracks);
    }

    public async Task DeletePlaylistTracksAsync(Guid jobId)
    {
        await _trackRepository.DeletePlaylistTracksAsync(jobId);
    }

    public async Task DeleteSinglePlaylistTrackAsync(Guid playlistTrackId)
    {
        await _trackRepository.DeleteSinglePlaylistTrackAsync(playlistTrackId);
    }

    /// <summary>
    /// Atomically saves a PlaylistJob and all its associated tracks in a single transaction.
    /// This ensures data integrity: either the entire job+tracks are saved, or none are.
    /// Called by DownloadManager.QueueProject() for imports.
    /// </summary>
    public async Task SavePlaylistJobWithTracksAsync(PlaylistJob job)
    {
        // Prevent race conditions with other DB writes
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            using var transaction = await context.Database.BeginTransactionAsync();
            
            try
            {
            // Convert model to entity
            // 2. Handle Job Header (Add or Update)
            // 2. Robust Upserter Strategy
            // Even with semaphore, we check first. If missing, we TRY ADD.
            // If that fails with Unique Constraint, we CATCH and UPDATE (Upsert).
            
            var existingJob = await context.Projects.FirstOrDefaultAsync(j => j.Id == job.Id);
            bool jobExists = existingJob != null;

            if (existingJob != null)
            {
                 // Update existing job logic
                 existingJob.TotalTracks = Math.Max(existingJob.TotalTracks, job.TotalTracks);
                 existingJob.SourceTitle = job.SourceTitle;
                 existingJob.SourceType = job.SourceType;
                 existingJob.IsDeleted = false;
                 
                 // Phase 20
                 existingJob.IsSmartPlaylist = job.IsSmartPlaylist;
                 existingJob.SmartCriteriaJson = job.SmartCriteriaJson;
                 
                 context.Projects.Update(existingJob);
            }
            else
            {
                 // Attempt Insert
                 var jobEntity = new PlaylistJobEntity
                 {
                     Id = job.Id,
                     SourceTitle = job.SourceTitle,
                     SourceType = job.SourceType,
                     DestinationFolder = job.DestinationFolder,
                     CreatedAt = job.CreatedAt,
                     TotalTracks = job.TotalTracks,
                     SuccessfulCount = job.SuccessfulCount,
                     FailedCount = job.FailedCount,
                     MissingCount = job.MissingCount,
                     // Duplicates removed
                     IsDeleted = false,
                     
                     // Phase 20
                     IsSmartPlaylist = job.IsSmartPlaylist,
                     SmartCriteriaJson = job.SmartCriteriaJson
                 };
                 context.Projects.Add(jobEntity);
                 
                 // Immediate save to catch Unique Constraint violation NOW
                 try
                 {
                     await context.SaveChangesAsync();
                     jobExists = true; // Mark as exists for track handling
                 }
                 catch (DbUpdateException dbEx) when (dbEx.InnerException?.Message.Contains("UNIQUE constraint failed") == true)
                 {
                     _logger.LogWarning("Caught Race Condition in PlaylistJob Insert! Switching to Update strategy for JobId {Id}", job.Id);
                     
                     // Detach the failed entity to clear context state
                     context.Entry(jobEntity).State = EntityState.Detached;

                     // Re-fetch the phantom existing job (might be soft-deleted)
                     existingJob = await context.Projects.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == job.Id);
                     if (existingJob != null)
                     {
                         // Un-delete if it was soft-deleted
                         existingJob.TotalTracks = Math.Max(existingJob.TotalTracks, job.TotalTracks);
                         existingJob.IsDeleted = false;
                         context.Projects.Update(existingJob);
                         await context.SaveChangesAsync(); // Save the UPDATE now
                         jobExists = true;
                         
                         // CRITICAL: Ensure jobEntity is NOT tracked as Added
                         // This prevents line 1525 from trying to INSERT it again
                         var trackedEntity = context.ChangeTracker.Entries<PlaylistJobEntity>()
                             .FirstOrDefault(e => e.Entity.Id == job.Id && e.State == EntityState.Added);
                         if (trackedEntity != null)
                         {
                             trackedEntity.State = EntityState.Detached;
                             _logger.LogDebug("Detached duplicate Added entity for JobId {Id}", job.Id);
                         }
                     }
                     else
                     {
                         throw; // Should be impossible
                     }
                 }
            }
            
            // Pre-fetch existing library entries for metadata inheritance
            // This ensures new playlist tracks inherit data from already enriched library items.
            var allHashes = job.PlaylistTracks
                .Select(t => t.TrackUniqueHash)
                .Where(h => !string.IsNullOrEmpty(h))
                .Distinct()
                .ToList();
                
            var libraryEntries = await context.LibraryEntries
                .Where(e => allHashes.Contains(e.UniqueHash))
                .ToDictionaryAsync(e => e.UniqueHash);

            // For tracks, we also need to handle Add vs Update. 
            if (!jobExists) // This branch is only taken if we successfully added a NEW job and it stayed new
            {
                var trackEntities = job.PlaylistTracks.Select(track => 
                {
                    // Inherit from Library if missing
                    libraryEntries.TryGetValue(track.TrackUniqueHash ?? "", out var entry);

                    return new PlaylistTrackEntity
                    {
                        Id = track.Id,
                        PlaylistId = job.Id,
                        Artist = track.Artist,
                        Title = track.Title,
                        Album = track.Album,
                        TrackUniqueHash = track.TrackUniqueHash ?? string.Empty,
                        Status = track.Status,
                        ResolvedFilePath = track.ResolvedFilePath,
                        TrackNumber = track.TrackNumber,
                        AddedAt = track.AddedAt,
                        SortOrder = track.SortOrder,
                        Priority = track.Priority,
                        SourcePlaylistId = track.SourcePlaylistId,
                        SourcePlaylistName = track.SourcePlaylistName,
                        
                        // Phase 0: Spotify Metadata (Inherit if possible)
                        SpotifyTrackId = track.SpotifyTrackId ?? entry?.SpotifyTrackId,
                        SpotifyAlbumId = track.SpotifyAlbumId ?? entry?.SpotifyAlbumId,
                        SpotifyArtistId = track.SpotifyArtistId ?? entry?.SpotifyArtistId,
                        AlbumArtUrl = track.AlbumArtUrl ?? entry?.AlbumArtUrl,
                        ArtistImageUrl = track.ArtistImageUrl, // Usually library entry doesn't have this yet
                        Genres = track.Genres ?? entry?.Genres,
                        Popularity = track.Popularity > 0 ? track.Popularity : (entry?.Popularity ?? 0),
                        CanonicalDuration = track.CanonicalDuration > 0 ? track.CanonicalDuration : (entry?.DurationSeconds ?? 0),
                        ReleaseDate = track.ReleaseDate,

                        // Phase 0.1: Musical Intelligence (Inherit if possible)
                        MusicalKey = !string.IsNullOrEmpty(track.MusicalKey) ? track.MusicalKey : entry?.MusicalKey,
                        BPM = track.BPM > 0 ? track.BPM : (entry?.BPM ?? 0),
                        Energy = track.Energy > 0 ? track.Energy : (entry?.Energy ?? 0),
                        Danceability = track.Danceability > 0 ? track.Danceability : (entry?.Danceability ?? 0),
                        Valence = track.Valence > 0 ? track.Valence : (entry?.Valence ?? 0),
                        
                        CuePointsJson = track.CuePointsJson,
                        AudioFingerprint = track.AudioFingerprint,
                        BitrateScore = track.BitrateScore,
                        AnalysisOffset = track.AnalysisOffset,

                        // Phase 8: Sonic Integrity
                        SpectralHash = track.SpectralHash,
                        QualityConfidence = track.QualityConfidence,
                        FrequencyCutoff = track.FrequencyCutoff,
                        IsTrustworthy = track.IsTrustworthy,
                        QualityDetails = track.QualityDetails,

                        // Phase 13: Search Filter Overrides
                        PreferredFormats = track.PreferredFormats,
                        MinBitrateOverride = track.MinBitrateOverride,
                        
                        IsEnriched = (track.IsEnriched || (entry?.IsEnriched ?? false))
                    };
                });
                context.PlaylistTracks.AddRange(trackEntities);
            }
            else
            {
                var trackIds = job.PlaylistTracks.Select(t => t.Id).ToList();
                var existingTrackIds = await context.PlaylistTracks
                    .Where(t => trackIds.Contains(t.Id))
                    .Select(t => t.Id)
                    .ToListAsync();
                var existingTrackIdSet = new HashSet<Guid>(existingTrackIds);

                foreach (var track in job.PlaylistTracks)
                {
                    // Inherit from Library if missing
                    libraryEntries.TryGetValue(track.TrackUniqueHash ?? "", out var entry);

                    var trackEntity = new PlaylistTrackEntity
                    {
                        Id = track.Id,
                        PlaylistId = job.Id,
                        Artist = track.Artist,
                        Title = track.Title,
                        Album = track.Album,
                        TrackUniqueHash = track.TrackUniqueHash ?? string.Empty,
                        Status = track.Status,
                        ResolvedFilePath = track.ResolvedFilePath,
                        TrackNumber = track.TrackNumber,
                        AddedAt = track.AddedAt,
                        SortOrder = track.SortOrder,
                        Priority = track.Priority,
                        SourcePlaylistId = track.SourcePlaylistId,
                        SourcePlaylistName = track.SourcePlaylistName,
                        
                        // Phase 0: Spotify Metadata (Inherit)
                        SpotifyTrackId = track.SpotifyTrackId ?? entry?.SpotifyTrackId,
                        SpotifyAlbumId = track.SpotifyAlbumId ?? entry?.SpotifyAlbumId,
                        SpotifyArtistId = track.SpotifyArtistId ?? entry?.SpotifyArtistId,
                        AlbumArtUrl = track.AlbumArtUrl ?? entry?.AlbumArtUrl,
                        ArtistImageUrl = track.ArtistImageUrl,
                        Genres = track.Genres ?? entry?.Genres,
                        Popularity = track.Popularity > 0 ? track.Popularity : (entry?.Popularity ?? 0),
                        CanonicalDuration = track.CanonicalDuration > 0 ? track.CanonicalDuration : (entry?.DurationSeconds ?? 0),
                        ReleaseDate = track.ReleaseDate,
                        
                        // Phase 0.1: Musical Intelligence (Inherit)
                        MusicalKey = !string.IsNullOrEmpty(track.MusicalKey) ? track.MusicalKey : entry?.MusicalKey,
                        BPM = track.BPM > 0 ? track.BPM : (entry?.BPM ?? 0),
                        Energy = track.Energy > 0 ? track.Energy : (entry?.Energy ?? 0),
                        Danceability = track.Danceability > 0 ? track.Danceability : (entry?.Danceability ?? 0),
                        Valence = track.Valence > 0 ? track.Valence : (entry?.Valence ?? 0),

                        CuePointsJson = track.CuePointsJson,
                        AudioFingerprint = track.AudioFingerprint,
                        BitrateScore = track.BitrateScore,
                        AnalysisOffset = track.AnalysisOffset,

                        // Phase 8: Sonic Integrity
                        SpectralHash = track.SpectralHash,
                        QualityConfidence = track.QualityConfidence,
                        FrequencyCutoff = track.FrequencyCutoff,
                        IsTrustworthy = track.IsTrustworthy,
                        QualityDetails = track.QualityDetails,

                        // Phase 13: Search Filter Overrides
                        PreferredFormats = track.PreferredFormats,
                        MinBitrateOverride = track.MinBitrateOverride,
                        
                        IsEnriched = (track.IsEnriched || (entry?.IsEnriched ?? false))
                    };

                    if (existingTrackIdSet.Contains(track.Id))
                    {
                        context.PlaylistTracks.Update(trackEntity);
                    }
                    else
                    {
                        context.PlaylistTracks.Add(trackEntity);
                    }
                }
            }
            
            await context.SaveChangesAsync();

            // Phase 2: Recalculate and update header counts to ensure accuracy after merges/updates.
            // This prevents "lost counts" when merging a small batch into a large existing playlist.
            var consolidatedJob = await context.Projects.FirstOrDefaultAsync(j => j.Id == job.Id);
            if (consolidatedJob != null)
            {
                // Source of truth is now the aggregate of all tracks for this PlaylistId in the DB
                consolidatedJob.TotalTracks = await context.PlaylistTracks.CountAsync(t => t.PlaylistId == job.Id);
                consolidatedJob.SuccessfulCount = await context.PlaylistTracks.CountAsync(t => t.PlaylistId == job.Id && t.Status == TrackStatus.Downloaded);
                consolidatedJob.FailedCount = await context.PlaylistTracks.CountAsync(t => t.PlaylistId == job.Id && (t.Status == TrackStatus.Failed || t.Status == TrackStatus.Skipped));
                consolidatedJob.MissingCount = await context.PlaylistTracks.CountAsync(t => t.PlaylistId == job.Id && t.Status == TrackStatus.Missing);
                
                context.Projects.Update(consolidatedJob);
                await context.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            
            _logger.LogInformation(
                "Atomically saved PlaylistJob '{Title}' ({Id}) with {TrackCount} tracks. Thread: {ThreadId}",
                job.SourceTitle,
                job.Id,
                job.PlaylistTracks.Count,
                Thread.CurrentThread.ManagedThreadId);
        }
        catch
        {
            throw;
        }
    }
    finally
    {
        _writeSemaphore.Release();
    }
    }

    public async Task LogPlaylistJobDiagnostic(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.Projects
            .AsNoTracking()
            .Include(j => j.Tracks)
            .FirstOrDefaultAsync(j => j.Id == jobId);

        if (job == null)
        {
            _logger.LogWarning("DIAGNOSTIC: JobId {JobId} not found.", jobId);
            return;
        }

        _logger.LogInformation(
            "DIAGNOSTIC for JobId {JobId}: Title='{SourceTitle}', IsDeleted={IsDeleted}, CreatedAt={CreatedAt}, TotalTracks={TotalTracks}",
            job.Id,
            job.SourceTitle,
            job.IsDeleted,
            job.CreatedAt,
            job.TotalTracks
        );

        foreach (var track in job.Tracks)
        {
            _logger.LogInformation(
                "  DIAGNOSTIC for Track {TrackId} in Job {JobId}: Artist='{Artist}', Title='{Title}', TrackUniqueHash='{TrackUniqueHash}', Status='{Status}'",
                track.Id,
                job.Id,
                track.Artist,
                track.Title,
                track.TrackUniqueHash,
                track.Status
            );
        }
    }

    public async Task<List<PlaylistTrackEntity>> GetAllPlaylistTracksAsync()
    {
        using var context = new AppDbContext();
        
        // Filter out tracks from soft-deleted jobs
        var validJobIds = context.Projects
            .Where(j => !j.IsDeleted)
            .Select(j => j.Id);
            
        return await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => validJobIds.Contains(t.PlaylistId))
            .OrderByDescending(t => t.AddedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Phase 3C.5: Lazy Hydration - Fetch pending tracks for the waiting room.
    /// Orders by Priority (0=High) then Time. LIMITs result to buffer size.
    /// </summary>
    public async Task<List<PlaylistTrackEntity>> GetPendingPriorityTracksAsync(int limit, List<Guid> excludeIds)
    {
        using var context = new AppDbContext();
        
        // 1. Get valid jobs
        var validJobIds = context.Projects
            .Where(j => !j.IsDeleted)
            .Select(j => j.Id);
            
        // 2. Query Pending (Status=0) tracks
        // Rule: Priority ASC (0, 1, 10...), then AddedAt ASC (FIFO)
        var query = context.PlaylistTracks
            .AsNoTracking()
            .Where(t => validJobIds.Contains(t.PlaylistId) && t.Status == TrackStatus.Missing);

        if (excludeIds.Any())
        {
            query = query.Where(t => !excludeIds.Contains(t.Id));
        }

        return await query
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>
    /// Phase 3C.5: Lazy Hydration - Fetch all non-pending tracks (History/Active).
    /// </summary>
    public async Task<List<PlaylistTrackEntity>> GetNonPendingTracksAsync()
    {
        using var context = new AppDbContext();
        var validJobIds = context.Projects.Where(j => !j.IsDeleted).Select(j => j.Id);
        
        return await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => validJobIds.Contains(t.PlaylistId) && t.Status != TrackStatus.Missing)
            .OrderByDescending(t => t.AddedAt)
            .ToListAsync();
    }

    public async Task LogActivityAsync(PlaylistActivityLogEntity log)
    {
        using var context = new AppDbContext();
        context.ActivityLogs.Add(log);
        await context.SaveChangesAsync();
    }

    public async Task<PlaylistActivityLogEntity?> GetLastPlaylistActivityAsync(Guid playlistId, string action)
    {
        using var context = new AppDbContext();
        return await context.ActivityLogs
            .Where(l => l.PlaylistId == playlistId && l.Action == action)
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefaultAsync();
    }

    public async Task DeleteActivityLogAsync(Guid logId)
    {
        using var context = new AppDbContext();
        var log = await context.ActivityLogs.FindAsync(logId);
        if (log != null)
        {
            context.ActivityLogs.Remove(log);
            await context.SaveChangesAsync();
        }
    }

    public async Task BatchDeletePlaylistTracksAsync(List<Guid> trackIds)
    {
        using var context = new AppDbContext();
        var tracks = await context.PlaylistTracks
            .Where(t => trackIds.Contains(t.Id))
            .ToListAsync();
        
        if (tracks.Any())
        {
            context.PlaylistTracks.RemoveRange(tracks);
            await context.SaveChangesAsync();
        }
    }


    /// <summary>
    /// Saves the current playback queue to the database.
    /// Clears existing queue and saves the new state.
    /// </summary>
    public async Task SaveQueueAsync(List<(Guid trackId, int position, bool isCurrent)> queueItems)
    {
        using var context = new AppDbContext();
        
        // Clear existing queue
        var existingQueue = await context.QueueItems.ToListAsync();
        context.QueueItems.RemoveRange(existingQueue);
        
        // Add new queue items
        foreach (var (trackId, position, isCurrent) in queueItems)
        {
            context.QueueItems.Add(new QueueItemEntity
            {
                PlaylistTrackId = trackId,
                QueuePosition = position,
                IsCurrentTrack = isCurrent,
                AddedAt = DateTime.UtcNow
            });
        }
        
        await context.SaveChangesAsync();
        _logger.LogInformation("Saved queue with {Count} items", queueItems.Count);
    }

    /// <summary>
    /// Loads the saved playback queue from the database.
    /// Returns queue items with their associated track data.
    /// </summary>
    public async Task<List<(PlaylistTrack track, bool isCurrent)>> LoadQueueAsync()
    {
        using var context = new AppDbContext();
        
        var queueItems = await context.QueueItems
            .OrderBy(q => q.QueuePosition)
            .ToListAsync();
            
        var trackIds = queueItems.Select(q => q.PlaylistTrackId).ToList();
        
        var trackEntities = await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => trackIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id); // Dictionary for O(1) lookup
            
        var result = new List<(PlaylistTrack, bool)>();
        
        foreach (var queueItem in queueItems)
        {
            if (trackEntities.TryGetValue(queueItem.PlaylistTrackId, out var trackEntity))
            {
                var track = new PlaylistTrack
                {
                    Id = trackEntity.Id,
                    PlaylistId = trackEntity.PlaylistId,
                    Artist = trackEntity.Artist,
                    Title = trackEntity.Title,
                    Album = trackEntity.Album,
                    TrackUniqueHash = trackEntity.TrackUniqueHash,
                    Status = trackEntity.Status,
                    ResolvedFilePath = trackEntity.ResolvedFilePath,
                    TrackNumber = trackEntity.TrackNumber,
                    Priority = trackEntity.Priority,
                    SourcePlaylistId = trackEntity.SourcePlaylistId,
                    SourcePlaylistName = trackEntity.SourcePlaylistName,
                    AddedAt = trackEntity.AddedAt,
                    SortOrder = trackEntity.SortOrder,
                    SpotifyTrackId = trackEntity.SpotifyTrackId,
                    AlbumArtUrl = trackEntity.AlbumArtUrl,
                    ArtistImageUrl = trackEntity.ArtistImageUrl,
                    // Map other fields as needed...
                };
                
                result.Add((track, queueItem.IsCurrentTrack));
            }
        }
        
        return result;

    }

    /// <summary>
    /// Clears the saved playback queue from the database.
    /// </summary>
    public async Task ClearQueueAsync()
    {
        using var context = new AppDbContext();
        var existingQueue = await context.QueueItems.ToListAsync();
        context.QueueItems.RemoveRange(existingQueue);
        await context.SaveChangesAsync();
        _logger.LogInformation("Cleared saved queue");
    }

    /// <summary>
    /// Phase 8: Maintenance - Vacuum database to reclaim space and optimize performance.
    /// Should be called periodically (e.g., during daily maintenance).
    /// </summary>
    public async Task VacuumDatabaseAsync()
    {
        try
        {
            using var context = new AppDbContext();
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync();
            
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM";
            await cmd.ExecuteNonQueryAsync();
            
            _logger.LogInformation("Database VACUUM completed successfully");
            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database VACUUM failed (this is non-critical)");
        }
    }

    /// <summary>
    /// ⚠️ INITIATING DATABASE RESET ⚠️
    /// Safely deletes the database file and re-initializes a fresh schema.
    /// This will wipe ALL tracks, projects, and cached data.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            _logger.LogWarning("⚠️ INITIATING DATABASE RESET ⚠️");

            using var context = new AppDbContext();
            
            // 1. Force close any lingering connections
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // 2. Delete the database file physically
            await context.Database.EnsureDeletedAsync();
            _logger.LogInformation("Database file deleted.");

            // 3. Re-initialize (creates fresh tables)
            await InitAsync();
            
            _logger.LogInformation("Database reset complete. System is fresh.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset database.");
            throw;
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// Updates a specific track's engagement metrics (Like status, Rating, PlayCount).
    /// Used by the Media Player UI.
    /// </summary>
    public async Task UpdatePlaylistTrackAsync(PlaylistTrackEntity track)
    {
        try 
        {
            using var context = new AppDbContext();
            const string sql = @"
                UPDATE PlaylistTracks 
                SET IsLiked = {0},
                    Rating = {1},
                    PlayCount = {2},
                    LastPlayedAt = {3},
                    Status = {4}
                WHERE Id = {5}";

            await context.Database.ExecuteSqlRawAsync(sql, 
                track.IsLiked, 
                track.Rating, 
                track.PlayCount, 
                (object?)track.LastPlayedAt ?? DBNull.Value, 
                track.Status.ToString(), 
                track.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update playlist track {Id}", track.Id);
            throw; // Re-throw to allow ViewModel to handle rollback
        }
    }

    /// <summary>
    /// Phase 3C: Bulk updates Priority for all tracks in a specific playlist.
    /// Used by DownloadManager.PrioritizeProjectAsync for queue orchestration.
    /// </summary>
    public async Task UpdatePlaylistTracksPriorityAsync(Guid playlistId, int newPriority)
    {
        await _trackRepository.UpdatePlaylistTracksPriorityAsync(playlistId, newPriority);
    }

    public async Task UpdatePlaylistTrackPriorityAsync(Guid trackId, int newPriority)
    {
        await _trackRepository.UpdatePlaylistTrackPriorityAsync(trackId, newPriority);
    }



    // ===== Phase 1B: WAL Mode & Index Optimization Methods =====

    /// <summary>
    /// Phase 1B: Manually triggers a WAL checkpoint to merge .wal file into main database.
    /// Useful during low-activity periods or before backups.
    /// </summary>
    public async Task CheckpointWalAsync()
    {
        try
        {
            using var context = new AppDbContext();
            var connection = context.Database.GetDbConnection() as SqliteConnection;
            await connection!.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            
            var result = await cmd.ExecuteScalarAsync();
            _logger.LogInformation("WAL checkpoint completed: {Result}", result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WAL checkpoint failed (non-fatal)");
        }
    }





    /// <summary>
    /// Phase 1B: Benchmarks database performance before/after WAL mode.
    /// </summary>
    public async Task<PerformanceBenchmark> BenchmarkDatabaseAsync()
    {
        var benchmark = new PerformanceBenchmark();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var context = new AppDbContext();
            
            // Test 1: Read 1000 tracks
            stopwatch.Restart();
            var tracks = await context.PlaylistTracks.Take(1000).ToListAsync();
            benchmark.Read1000TracksMs = stopwatch.ElapsedMilliseconds;
            
            // Test 2: Filtered query (common pattern)
            stopwatch.Restart();
            var filtered = await context.PlaylistTracks
                .Where(t => t.Status == TrackStatus.Downloaded)
                .Take(100)
                .ToListAsync();
            benchmark.FilteredQueryMs = stopwatch.ElapsedMilliseconds;
            
            // Test 3: Join query (library entries)
            stopwatch.Restart();
            var joined = await context.LibraryEntries
                .Take(100)
                .ToListAsync();
            benchmark.JoinQueryMs = stopwatch.ElapsedMilliseconds;
            
            _logger.LogInformation(
                "Benchmark: Read={Read}ms, Filter={Filter}ms, Join={Join}ms",
                benchmark.Read1000TracksMs,
                benchmark.FilteredQueryMs,
                benchmark.JoinQueryMs);
            
            return benchmark;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Benchmark failed");
            throw;
        }
    }

    /// <summary>
    /// Closes all database connections and disposes resources during application shutdown.
    /// Prevents orphaned connections and ensures clean process termination.
    /// Note: DatabaseService uses 'using var context' pattern, so no persistent context to dispose.
    /// </summary>
    public async Task CloseConnectionsAsync()
    {
        _logger.LogInformation("Database service shutdown - Running WAL Checkpoint...");
        
        // Sprint 5C Hardening: Retry loop for WAL checkpoint
        int attempts = 0;
        const int maxAttempts = 3;
        while (attempts < maxAttempts)
        {
            try
            {
                using var context = new AppDbContext();
                // Phase 5C Hardening: Checkpoint WAL on shutdown to merge -wal file
                await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(FULL);");
                _logger.LogInformation("WAL Checkpoint complete.");
                return;
            }
            catch (Exception ex)
            {
                attempts++;
                _logger.LogWarning(ex, "WAL Checkpoint attempt {Attempt}/{Max} failed. Retrying in 1s...", attempts, maxAttempts);
                if (attempts >= maxAttempts)
                {
                    _logger.LogError("WAL Checkpoint FAILED after {Max} attempts. Forcing closure.", maxAttempts);
                    break;
                }
                await Task.Delay(1000);
            }
        }
    }
    /// <summary>
    /// Retrieves all library entries. Used for bulk operations like Export.
    /// WARN: This can be memory intensive for large libraries.
    /// </summary>
    public async Task<List<LibraryEntryEntity>> GetAllLibraryEntriesAsync()
    {
        using var context = new AppDbContext();
        // Load *all* entries, no filtering (for Global Index)
        return await context.LibraryEntries
            .AsNoTracking()
            .Include(e => e.AudioFeatures) // Phase 21: Eager load Brain data
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesByHashesAsync(List<string> hashes)
    {
        if (hashes == null || !hashes.Any()) return new List<LibraryEntryEntity>();
        
        using var context = new AppDbContext();
        // Chunk requests to avoid SQL parameter limits
        var results = new List<LibraryEntryEntity>();
        var chunks = hashes.Chunk(500);
        
        foreach (var chunk in chunks)
        {
            var chunkList = chunk.ToList();
            var entries = await context.LibraryEntries.AsNoTracking()
                .Include(e => e.AudioFeatures) // Phase 21: Eager load Brain data
                .Where(e => chunkList.Contains(e.UniqueHash))
                .ToListAsync()
                .ConfigureAwait(false);
            results.AddRange(entries);
        }
        
        return results;
    }

    public async Task<List<LibraryEntryEntity>> SearchLibraryEntriesWithStatusAsync_Renamed(string query, int limit = 50)
    {
        return await _trackRepository.GetLibraryEntriesNeedingGenresAsync(limit);
    }

    // ===== Genre Enrichment Methods (Stage 3) =====

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingGenresAsync(int limit)
    {
        return await _trackRepository.GetLibraryEntriesNeedingGenresAsync(limit);
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingGenresAsync(int limit)
    {
        return await _trackRepository.GetPlaylistTracksNeedingGenresAsync(limit);
    }

    public async Task UpdateLibraryEntriesGenresAsync(Dictionary<string, List<string>> artistGenreMap)
    {
        await _trackRepository.UpdateLibraryEntriesGenresAsync(artistGenreMap);
    }
    // Phase 15: Style Lab
    public async Task<List<StyleDefinitionEntity>> LoadAllStyleDefinitionsAsync()
    {
        using var context = new AppDbContext();
        return await context.StyleDefinitions.AsNoTracking().ToListAsync();
    }

    // Phase 16.2: Vibe Match
    public async Task<List<AudioFeaturesEntity>> LoadAllAudioFeaturesAsync()
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            return await context.AudioFeatures.AsNoTracking().ToListAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load all audio features");
            return new List<AudioFeaturesEntity>();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }



    /// <summary>
    /// Phase 11.5: Marks a track as verified:
    /// 1. Updates IsReviewNeeded = false
    /// 2. Updates CurationConfidence to High (Verified)
    /// 3. Sets Source to Manual
    /// </summary>
    public async Task MarkTrackAsVerifiedAsync(string trackHash)
    {
        await _trackRepository.MarkTrackAsVerifiedAsync(trackHash);
    }

    public async Task<TrackTechnicalEntity?> GetTrackTechnicalDetailsAsync(Guid playlistTrackId)
    {
        return await _trackRepository.GetTrackTechnicalDetailsAsync(playlistTrackId);
    }

    public async Task SaveTechnicalDetailsAsync(TrackTechnicalEntity details)
    {
        await _trackRepository.SaveTechnicalDetailsAsync(details);
    }
    /// <summary>
    /// Gets existing AudioFeatures for the given track hash.
    /// </summary>
    public async Task<AudioFeaturesEntity?> GetAudioFeaturesByHashAsync(string uniqueHash)
    {
        using var context = new AppDbContext();
        return await context.AudioFeatures.AsNoTracking().FirstOrDefaultAsync(f => f.TrackUniqueHash == uniqueHash);
    }

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingMusicBrainzEnrichmentAsync(int count)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Where(e => !string.IsNullOrEmpty(e.ISRC) && string.IsNullOrEmpty(e.MusicBrainzId))
            .Take(count)
            .ToListAsync();
    }

    public async Task UpdateAudioFeaturesAsync(AudioFeaturesEntity features)
    {
        using var context = new AppDbContext();
        var existing = await context.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == features.TrackUniqueHash);

        if (existing != null)
        {
            context.Entry(existing).CurrentValues.SetValues(features);
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Finds AudioFeatures by file path.
    /// Used by PersonalClassifierService to classify tracks on the fly.
    /// </summary>
    public async Task<AudioFeaturesEntity?> GetAudioFeaturesAsync(string filePath)
    {
        using var context = new AppDbContext();
        
        // 1. Try to find a LibraryEntry with this path
        var libraryEntry = await context.LibraryEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.FilePath == filePath);

        if (libraryEntry != null)
        {
            return await GetAudioFeaturesByHashAsync(libraryEntry.UniqueHash);
        }

        // 2. Try to find a PlaylistTrack with this path
        var track = await context.PlaylistTracks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.ResolvedFilePath == filePath);

        if (track != null)
        {
            return await GetAudioFeaturesByHashAsync(track.TrackUniqueHash);
        }

        return null;
    }

    // ===== Backup Methods =====

    public async Task BackupDatabaseAsync(string backupPath)
    {
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "library.db");
        
        if (!File.Exists(dbPath))
        {
            _logger.LogWarning("Database file not found at {Path}, skipping backup", dbPath);
            return;
        }

        try
        {
            await _fileWriteService.CopyFileAtomicAsync(dbPath, backupPath, preserveTimestamps: true);
            _logger.LogInformation("✅ Database backup created successfully at {Path}", backupPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create database backup at {Path}", backupPath);
        }
    }

    public async Task<List<PlaylistTrackEntity>> SearchPlaylistTracksAsync(string query, int limit = 50)
    {
        return await _trackRepository.SearchPlaylistTracksAsync(query, limit);
    }

    public async Task<List<PlaylistTrackEntity>> FindTracksInOtherProjectsAsync(
        string artist, string title, Guid excludeProjectId)
    {
        return await _trackRepository.FindTracksInOtherProjectsAsync(artist, title, excludeProjectId);
    }
}



