using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SLSKDONET.Models;
using SLSKDONET.Services.Models;

namespace SLSKDONET.Services.Repositories;

public class TrackRepository : ITrackRepository
{
    private readonly ILogger<TrackRepository> _logger;
    private static readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

    public TrackRepository(ILogger<TrackRepository> logger)
    {
        _logger = logger;
    }

    public async Task<List<TrackEntity>> LoadTracksAsync()
    {
        using var context = new AppDbContext();
        return await context.Tracks.ToListAsync();
    }

    public async Task<TrackEntity?> FindTrackAsync(string globalId)
    {
        using var context = new AppDbContext();
        return await context.Tracks.FirstOrDefaultAsync(t => t.GlobalId == globalId);
    }

    public async Task SaveTrackAsync(TrackEntity track)
    {
        const int maxRetries = 5;
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var existingTrack = await context.Tracks
                        .FirstOrDefaultAsync(t => t.GlobalId == track.GlobalId);

                    if (existingTrack == null)
                    {
                        context.Tracks.Add(track);
                    }
                    else
                    {
                        // Update properties
                        context.Entry(existingTrack).CurrentValues.SetValues(track);
                    }

                    await context.SaveChangesAsync();
                    return;
                }
                catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 5)
                {
                    if (attempt < maxRetries - 1)
                    {
                        _logger.LogWarning("SQLite database locked saving track {GlobalId}, attempt {Attempt}/{Max}. Retrying...", track.GlobalId, attempt + 1, maxRetries);
                        await Task.Delay(100 * (attempt + 1));
                        continue;
                    }
                    throw;
                }
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task UpdateTrackFilePathAsync(string globalId, string newPath)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var track = await context.Tracks.FirstOrDefaultAsync(t => t.GlobalId == globalId);
            if (track != null)
            {
                track.Filename = newPath;
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task RemoveTrackAsync(string globalId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var track = await context.Tracks.FirstOrDefaultAsync(t => t.GlobalId == globalId);
            if (track != null)
            {
                context.Tracks.Remove(track);
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<PlaylistTrackEntity>> LoadPlaylistTracksAsync(Guid playlistId)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .Include(t => t.TechnicalDetails) 
            .Include(t => t.AudioFeatures) // Phase 21: Eager load Brain data
            .Where(t => t.PlaylistId == playlistId)
            .OrderBy(t => t.SortOrder)
            .ToListAsync();
    }

    public async Task<PlaylistTrackEntity?> GetPlaylistTrackByHashAsync(Guid playlistId, string hash)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .Include(t => t.AudioFeatures)
            .FirstOrDefaultAsync(t => t.PlaylistId == playlistId && t.TrackUniqueHash == hash);
    }

    public async Task SavePlaylistTrackAsync(PlaylistTrackEntity track)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var existing = await context.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == track.Id);
            if (existing == null)
            {
                context.PlaylistTracks.Add(track);
            }
            else
            {
                context.Entry(existing).CurrentValues.SetValues(track);
            }
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<PlaylistTrackEntity>> GetAllPlaylistTracksAsync()
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks.ToListAsync();
    }

    public async Task<int> GetPlaylistTrackCountAsync(Guid playlistId, string? filter = null, bool? downloadedOnly = null)
    {
        using var context = new AppDbContext();
        var query = context.PlaylistTracks.AsQueryable();
        if (playlistId != Guid.Empty)
        {
            query = query.Where(t => t.PlaylistId == playlistId);
        }
        query = ApplyFilters(query, filter, downloadedOnly);
        return await query.CountAsync();
    }

    public async Task<List<PlaylistTrackEntity>> GetPagedPlaylistTracksAsync(Guid playlistId, int skip, int take, string? filter = null, bool? downloadedOnly = null)
    {
        using var context = new AppDbContext();
        var query = context.PlaylistTracks
            .Include(t => t.AudioFeatures)
            .AsNoTracking()
            .AsQueryable();
            
        if (playlistId != Guid.Empty)
        {
            query = query.Where(t => t.PlaylistId == playlistId);
        }
            
        query = ApplyFilters(query, filter, downloadedOnly);
        
        return await query
            .OrderBy(t => t.SortOrder)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
    }

    private IQueryable<PlaylistTrackEntity> ApplyFilters(IQueryable<PlaylistTrackEntity> query, string? filter, bool? downloadedOnly)
    {
        if (!string.IsNullOrEmpty(filter))
        {
            var lowerFilter = filter.ToLower();
            query = query.Where(t => t.Artist.ToLower().Contains(lowerFilter) || 
                                     t.Title.ToLower().Contains(lowerFilter) ||
                                     (t.MusicalKey != null && t.MusicalKey.ToLower().Contains(lowerFilter)));
        }
        if (downloadedOnly.HasValue)
        {
            if (downloadedOnly.Value)
                query = query.Where(t => t.Status == TrackStatus.Downloaded);
            else
                query = query.Where(t => t.Status != TrackStatus.Downloaded);
        }
        return query;
    }

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingEnrichmentAsync(int limit)
    {
        using var context = new AppDbContext();
        var cooldownDate = DateTime.UtcNow.AddHours(-4).ToString("O");
        
        return await context.LibraryEntries
            .Where(e => !e.IsEnriched 
                       && (e.SpotifyTrackId == null || e.SpotifyTrackId == "")
                       && e.SpotifyTrackId != "FAILED"
                       && (e.LastEnrichmentAttempt == null || e.LastEnrichmentAttempt.CompareTo(cooldownDate) < 0))
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdateLibraryEntryEnrichmentAsync(string uniqueHash, TrackEnrichmentResult result)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var existing = await context.LibraryEntries.FindAsync(uniqueHash);
            if (existing != null)
            {
                // Phase 21: Smart Retry - Track attempts and timestamp
                existing.EnrichmentAttempts = existing.EnrichmentAttempts + 1;
                existing.LastEnrichmentAttempt = DateTime.UtcNow.ToString("O");
                
                if (result.Success)
                {
                    existing.SpotifyTrackId = result.SpotifyId;
                    existing.SpotifyAlbumId = result.SpotifyAlbumId;
                    existing.SpotifyArtistId = result.SpotifyArtistId;
                    if (!string.IsNullOrEmpty(result.ISRC)) existing.ISRC = result.ISRC;
                    if (!string.IsNullOrEmpty(result.AlbumArtUrl)) existing.AlbumArtUrl = result.AlbumArtUrl;
                    
                    if (result.Bpm > 0 || !string.IsNullOrEmpty(result.MusicalKey))
                    {
                        existing.BPM = result.Bpm;
                        existing.Energy = result.Energy;
                        existing.Valence = result.Valence;
                        existing.Danceability = result.Danceability;
                        if (!string.IsNullOrEmpty(result.MusicalKey)) existing.MusicalKey = result.MusicalKey;
                        
                        // Phase 12.7: Style Classification
                        if (!string.IsNullOrEmpty(result.DetectedSubGenre)) existing.DetectedSubGenre = result.DetectedSubGenre;
                        if (result.SubGenreConfidence > 0) existing.SubGenreConfidence = result.SubGenreConfidence;
                    }
                    
                    // Reset retry tracking on success
                    existing.EnrichmentAttempts = 0;
                    existing.LastEnrichmentAttempt = null;
                }
                else
                {
                    // Phase 21: Only mark as permanently FAILED after max attempts (5)
                    const int MaxAttempts = 5;
                    if (existing.EnrichmentAttempts >= MaxAttempts)
                    {
                        existing.SpotifyTrackId = "FAILED";
                        existing.IsEnriched = true; // Stop retrying
                        _logger.LogWarning("Marking LibraryEntry {Hash} as permanently FAILED after {Attempts} attempts", uniqueHash, existing.EnrichmentAttempts);
                    }
                    else
                    {
                        _logger.LogInformation("LibraryEntry {Hash} enrichment failed (attempt {Attempt}/{Max}), will retry after cooldown", 
                            uniqueHash, existing.EnrichmentAttempts, MaxAttempts);
                    }
                }
                
                // Stage 2 (Features) is removed, so identification success is enough to mark as Enriched
                existing.IsEnriched = result.Success || (existing.SpotifyTrackId == "FAILED");
                
                // Sync with master Track record
                var tr = await context.Tracks.FindAsync(uniqueHash);
                if (tr != null)
                {
                    ApplyMetadata(tr, result);
                }

                existing.LastUsedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingEnrichmentAsync(int limit)
    {
        using var context = new AppDbContext();
        var cooldownDate = DateTime.UtcNow.AddHours(-4).ToString("O");

        return await context.PlaylistTracks
            .Where(e => !e.IsEnriched
                       && (e.SpotifyTrackId == null || e.SpotifyTrackId == "")
                       && e.SpotifyTrackId != "FAILED"
                       && (e.LastEnrichmentAttempt == null || e.LastEnrichmentAttempt.CompareTo(cooldownDate) < 0))
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdatePlaylistTrackEnrichmentAsync(Guid id, TrackEnrichmentResult result)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var track = await context.PlaylistTracks.FindAsync(id);
            if (track != null)
            {
                // Phase 21: Smart Retry - Track attempts and timestamp
                track.EnrichmentAttempts = track.EnrichmentAttempts + 1;
                track.LastEnrichmentAttempt = DateTime.UtcNow.ToString("O");
                
                if (result.Success)
                {
                    track.SpotifyTrackId = result.SpotifyId;
                    track.SpotifyAlbumId = result.SpotifyAlbumId;
                    track.SpotifyArtistId = result.SpotifyArtistId;
                    if (!string.IsNullOrEmpty(result.ISRC)) track.ISRC = result.ISRC;
                    if (!string.IsNullOrEmpty(result.AlbumArtUrl)) track.AlbumArtUrl = result.AlbumArtUrl;
                    if (result.Bpm > 0 || !string.IsNullOrEmpty(result.MusicalKey))
                    {
                        track.BPM = result.Bpm;
                        track.Energy = result.Energy;
                        track.Valence = result.Valence;
                        track.Danceability = result.Danceability;
                        if (!string.IsNullOrEmpty(result.MusicalKey)) track.MusicalKey = result.MusicalKey;

                        // Phase 12.7: Style Classification
                        if (!string.IsNullOrEmpty(result.DetectedSubGenre)) track.DetectedSubGenre = result.DetectedSubGenre;
                        if (result.SubGenreConfidence > 0) track.SubGenreConfidence = result.SubGenreConfidence;
                    }
                    
                    // Reset retry tracking on success
                    track.EnrichmentAttempts = 0;
                    track.LastEnrichmentAttempt = null;
                }
                else
                {
                    // Phase 21: Only mark as permanently FAILED after max attempts (5)
                    const int MaxAttempts = 5;
                    if (track.EnrichmentAttempts >= MaxAttempts)
                    {
                        track.SpotifyTrackId = "FAILED";
                        track.IsEnriched = true; // Stop retrying
                        _logger.LogWarning("Marking PlaylistTrack {Id} as permanently FAILED after {Attempts} attempts", id, track.EnrichmentAttempts);
                    }
                    else
                    {
                        _logger.LogInformation("PlaylistTrack {Id} enrichment failed (attempt {Attempt}/{Max}), will retry after cooldown", 
                            id, track.EnrichmentAttempts, MaxAttempts);
                    }
                }

                // If identification failed, mark as enriched to stop the cycle.
                // If it succeeded but we don't have features yet, Stage 2 will pick it up (IsEnriched is still false).
                // If Success=true, Stage 2 (Features) will pick it up because SpotifyTrackId is not null but IsEnriched is false.
                // We DON'T set IsEnriched=true here unless we truly have features or reached MaxAttempts.
                track.IsEnriched = (result.Success && result.Bpm > 0) || (track.SpotifyTrackId == "FAILED");
                
                // Sync with master Track record
                var tr = await context.Tracks.FindAsync(track.TrackUniqueHash);
                if (tr != null)
                {
                    ApplyMetadata(tr, result);
                }

                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<Guid>> UpdatePlaylistTrackStatusAndRecalculateJobsAsync(string trackUniqueHash, TrackStatus newStatus, string? resolvedPath, int searchRetryCount = 0, int notFoundRestartCount = 0)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            // 1. Find all PlaylistTrack entries for this global track hash
            var playlistTracks = await context.PlaylistTracks
                .Where(pt => pt.TrackUniqueHash == trackUniqueHash)
                .ToListAsync();

            if (playlistTracks.Count == 0) return new List<Guid>();

            var distinctJobIds = playlistTracks.Select(pt => pt.PlaylistId).Distinct().Cast<Guid>().ToList();

            // 2. Update their status
            foreach (var pt in playlistTracks)
            {
                pt.Status = newStatus;
                pt.SearchRetryCount = searchRetryCount;
                pt.NotFoundRestartCount = notFoundRestartCount;
                
                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    pt.ResolvedFilePath = resolvedPath;
                }
            }
            
            // Sync with master Track record if it exists
            var masterTrack = await context.Tracks.FindAsync(trackUniqueHash);
            if (masterTrack != null)
            {
                masterTrack.SearchRetryCount = searchRetryCount;
                masterTrack.NotFoundRestartCount = notFoundRestartCount;
            }
            
            // 3. Fetch all affected jobs and all their related tracks
            var jobsToUpdate = await context.Projects
                .Where(j => distinctJobIds.Contains(j.Id))
                .ToListAsync();

            var allRelatedTracks = await context.PlaylistTracks
                .Where(t => distinctJobIds.Contains(t.PlaylistId))
                .AsNoTracking()
                .ToListAsync();

            // 4. Recalculate counts for each job
            foreach (var job in jobsToUpdate)
            {
                var currentJobTracks = allRelatedTracks
                    .Where(t => t.PlaylistId == job.Id && t.TrackUniqueHash != trackUniqueHash)
                    .ToList();
                currentJobTracks.AddRange(playlistTracks.Where(pt => pt.PlaylistId == job.Id));

                job.SuccessfulCount = currentJobTracks.Count(t => t.Status == TrackStatus.Downloaded);
                job.FailedCount = currentJobTracks.Count(t => t.Status == TrackStatus.Failed || t.Status == TrackStatus.Skipped);
            }

            // 5. Update Library Health stats
            await UpdateLibraryHealthAsync(context);

            await context.SaveChangesAsync();
            return distinctJobIds;
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task SavePlaylistTracksAsync(IEnumerable<PlaylistTrackEntity> tracks)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            foreach (var t in tracks)
            {
                var existing = await context.PlaylistTracks.FindAsync(t.Id);
                if (existing == null) context.PlaylistTracks.Add(t);
                else context.Entry(existing).CurrentValues.SetValues(t);
            }
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task DeletePlaylistTracksAsync(Guid playlistId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var tracks = await context.PlaylistTracks.Where(t => t.PlaylistId == playlistId).ToListAsync();
            context.PlaylistTracks.RemoveRange(tracks);
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task UpdatePlaylistTracksPriorityAsync(Guid playlistId, int newPriority)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var tracks = await context.PlaylistTracks
                .Where(t => t.PlaylistId == playlistId && t.Status == TrackStatus.Missing)
                .ToListAsync();
            
            foreach (var track in tracks)
            {
                track.Priority = newPriority;
            }
            
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task UpdatePlaylistTrackPriorityAsync(Guid trackId, int newPriority)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var track = await context.PlaylistTracks.FindAsync(trackId);
            if (track != null)
            {
                track.Priority = newPriority;
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task DeleteSinglePlaylistTrackAsync(Guid trackId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var track = await context.PlaylistTracks.FindAsync(trackId);
            if (track != null)
            {
                context.PlaylistTracks.Remove(track);
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<TrackTechnicalEntity?> GetTrackTechnicalDetailsAsync(Guid playlistTrackId)
    {
        using var context = new AppDbContext();
        return await context.TechnicalDetails.FirstOrDefaultAsync(t => t.PlaylistTrackId == playlistTrackId);
    }

    public async Task<TrackTechnicalEntity> GetOrCreateTechnicalDetailsAsync(Guid playlistTrackId)
    {
        using var context = new AppDbContext();
        var existing = await context.TechnicalDetails.FirstOrDefaultAsync(t => t.PlaylistTrackId == playlistTrackId);
        
        if (existing != null)
            return existing;

        return new TrackTechnicalEntity
        {
            Id = Guid.NewGuid(),
            PlaylistTrackId = playlistTrackId,
            LastUpdated = DateTime.UtcNow
        };
    }

    public async Task SaveTechnicalDetailsAsync(TrackTechnicalEntity details)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var existing = await context.TechnicalDetails.FindAsync(details.Id);
            if (existing == null) context.TechnicalDetails.Add(details);
            else context.Entry(existing).CurrentValues.SetValues(details);
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<LibraryEntryEntity>> GetAllLibraryEntriesAsync()
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries.AsNoTracking().ToListAsync();
    }

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingGenresAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .AsNoTracking()
            .Where(e => !string.IsNullOrEmpty(e.SpotifyArtistId) && e.Genres == null)
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingGenresAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => !string.IsNullOrEmpty(t.SpotifyArtistId) && t.Genres == null)
            .OrderByDescending(t => t.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdateLibraryEntriesGenresAsync(Dictionary<string, List<string>> artistGenreMap)
    {
        if (!artistGenreMap.Any()) return;

        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var artistIds = artistGenreMap.Keys.ToList();
            
            var entries = await context.LibraryEntries
                .Where(e => !string.IsNullOrEmpty(e.SpotifyArtistId) && artistIds.Contains(e.SpotifyArtistId))
                .ToListAsync();

            foreach (var entry in entries)
            {
                if (artistGenreMap.TryGetValue(entry.SpotifyArtistId!, out var genres))
                {
                    entry.Genres = string.Join(", ", genres);
                }
            }

            var tracks = await context.PlaylistTracks
                .Where(t => !string.IsNullOrEmpty(t.SpotifyArtistId) && artistIds.Contains(t.SpotifyArtistId))
                .ToListAsync();

            foreach (var track in tracks)
            {
                if (artistGenreMap.TryGetValue(track.SpotifyArtistId!, out var genres))
                {
                    track.Genres = string.Join(", ", genres);
                }
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task MarkTrackAsVerifiedAsync(string trackHash)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var tracks = await context.PlaylistTracks
                .Include(pt => pt.TechnicalDetails)
                .Where(pt => pt.TrackUniqueHash == trackHash)
                .ToListAsync();
                
            foreach (var track in tracks)
            {
                if (track.TechnicalDetails != null)
                {
                    track.TechnicalDetails.IsReviewNeeded = false;
                }
            }

            var features = await context.AudioFeatures
                .FirstOrDefaultAsync(f => f.TrackUniqueHash == trackHash);
                
            if (features != null)
            {
                features.CurationConfidence = CurationConfidence.High;
                features.Source = DataSource.Manual;
                
                var provenance = new 
                {
                     Action = "Verified",
                     By = "User",
                     Timestamp = DateTime.UtcNow
                };
                features.ProvenanceJson = System.Text.Json.JsonSerializer.Serialize(provenance);
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private async Task UpdateLibraryHealthAsync(AppDbContext context)
    {
        try
        {
            var totalTracks = await context.PlaylistTracks.CountAsync();
            var hqTracks = await context.PlaylistTracks.CountAsync(t => t.Bitrate >= 256 || (t.Format != null && t.Format.ToLower() == "flac"));
            var lowBitrateTracks = await context.PlaylistTracks.CountAsync(t => t.Status == TrackStatus.Downloaded && t.Bitrate > 0 && t.Bitrate < 256);
            
            var health = await context.LibraryHealth.FindAsync(1) ?? new LibraryHealthEntity { Id = 1 };
            
            health.TotalTracks = totalTracks;
            health.HqTracks = hqTracks;
            health.UpgradableCount = lowBitrateTracks;
            health.LastScanDate = DateTime.Now;
            
            if (context.Entry(health).State == EntityState.Detached)
            {
                context.LibraryHealth.Add(health);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update library health cache during track update");
        }
    }

    public async Task<int> GetTotalLibraryTrackCountAsync(string? filter = null, bool? downloadedOnly = null)
    {
        using var context = new AppDbContext();
        
        if (!string.IsNullOrEmpty(filter))
        {
            var formattedSearch = filter.Trim() + "*";
            var count = await context.Database.ExecuteSqlRawAsync(
                "SELECT COUNT(*) FROM LibraryEntries WHERE rowid IN (SELECT rowid FROM LibraryEntriesFts WHERE LibraryEntriesFts MATCH {0})", formattedSearch);
            
            // ExecuteSqlRawAsync returns the number of rows affected, not the count.
            // We need to use a Different approach for Scalar results or just use the query.
            
            var query = context.LibraryEntries.FromSqlRaw(
                "SELECT * FROM LibraryEntries WHERE rowid IN (SELECT rowid FROM LibraryEntriesFts WHERE LibraryEntriesFts MATCH {0})", formattedSearch);
            
            if (downloadedOnly == true)
            {
                query = query.Where(t => t.FilePath != null && t.FilePath != "");
            }
            
            return await query.CountAsync();
        }

        var baseQuery = context.LibraryEntries.AsQueryable();
        if (downloadedOnly == true)
        {
            baseQuery = baseQuery.Where(t => t.FilePath != null && t.FilePath != "");
        }

        return await baseQuery.CountAsync();
    }

    public async Task<List<PlaylistTrackEntity>> GetPagedAllTracksAsync(int skip, int take, string? filter = null, bool? downloadedOnly = null)
    {
        using var context = new AppDbContext();
        
        IQueryable<LibraryEntryEntity> query;

        // 2. Apply Filters (Use FTS5 if filter is present)
        if (!string.IsNullOrEmpty(filter))
        {
            var formattedSearch = filter.Trim() + "*";
            query = context.LibraryEntries
                .FromSqlRaw("SELECT * FROM LibraryEntries WHERE rowid IN (SELECT rowid FROM LibraryEntriesFts WHERE LibraryEntriesFts MATCH {0})", formattedSearch);
        }
        else
        {
            query = context.LibraryEntries.AsQueryable();
        }

        query = query.Include(le => le.AudioFeatures).AsNoTracking();

        // 3. Apply DownloadedOnly
        if (downloadedOnly.HasValue)
        {
            if (downloadedOnly.Value)
                query = query.Where(t => t.FilePath != null && t.FilePath != "");
            else
                query = query.Where(t => string.IsNullOrEmpty(t.FilePath));
        }

        // 4. Order & Page (Optimized: Select only what's needed for the list view, avoiding heavy blobs)
        var entries = await query
            .OrderByDescending(t => t.AddedAt)
            .Skip(skip)
            .Take(take)
            .Select(e => new 
            {
                e.UniqueHash,
                e.Artist,
                e.Title,
                e.Album,
                e.FilePath,
                e.Bitrate,
                e.DurationSeconds,
                e.Format,
                e.AddedAt,
                e.LastUsedAt,
                e.SpotifyTrackId,
                e.ISRC,
                e.SpotifyAlbumId,
                e.SpotifyArtistId,
                e.AlbumArtUrl,
                e.Genres,
                e.Popularity,
                e.CanonicalDuration,
                e.ReleaseDate,
                e.Label,
                e.Comments,
                e.MusicalKey,
                e.BPM,
                e.Energy,
                e.Valence,
                e.Danceability,
                e.Integrity,
                e.Loudness,
                e.TruePeak,
                e.DynamicRange,
                e.IsEnriched,
                e.IsPrepared,
                e.PrimaryGenre,
                e.DetectedSubGenre,
                e.SubGenreConfidence,
                e.InstrumentalProbability,
                e.DropTimestamp,
                e.ManualEnergy,
                e.SourceProvenance,
                e.Rating,
                e.IsLiked,
                e.PlayCount,
                e.LastPlayedAt,
                e.AudioFeatures
            })
            .ToListAsync();

        // 5. PROJECT to PlaylistTrackEntity (The Adapter Pattern)
        // Re-mapping from the anonymous type to the entity
        return entries.Select(e => new PlaylistTrackEntity
        {
            Id = Guid.NewGuid(),
            PlaylistId = Guid.Empty,
            Artist = e.Artist,
            Title = e.Title,
            Album = e.Album,
            TrackUniqueHash = e.UniqueHash,
            Status = string.IsNullOrEmpty(e.FilePath) ? TrackStatus.Missing : TrackStatus.Downloaded,
            ResolvedFilePath = e.FilePath,
            SpotifyTrackId = e.SpotifyTrackId,
            AlbumArtUrl = e.AlbumArtUrl,
            BPM = (e.AudioFeatures?.Bpm > 0) ? e.AudioFeatures.Bpm : e.BPM,
            Energy = (e.AudioFeatures?.Energy > 0) ? e.AudioFeatures.Energy : e.Energy,
            Danceability = (e.AudioFeatures?.Danceability > 0) ? e.AudioFeatures.Danceability : e.Danceability,
            Valence = (e.AudioFeatures?.Valence > 0) ? e.AudioFeatures.Valence : e.Valence,
            MusicalKey = !string.IsNullOrEmpty(e.AudioFeatures?.Key) ? e.AudioFeatures.Key : e.MusicalKey,
            CanonicalDuration = e.DurationSeconds * 1000,
            SortOrder = 0,
            AddedAt = e.AddedAt,
            IsEnriched = e.IsEnriched,
            Bitrate = e.Bitrate,
            Format = e.Format,
            Integrity = e.Integrity,
            BitrateScore = e.Bitrate,
            DetectedSubGenre = e.AudioFeatures?.DetectedSubGenre ?? e.DetectedSubGenre,
            SubGenreConfidence = e.AudioFeatures?.SubGenreConfidence ?? e.SubGenreConfidence,
            InstrumentalProbability = e.AudioFeatures?.InstrumentalProbability ?? e.InstrumentalProbability,
            PrimaryGenre = e.PrimaryGenre,
            AudioFeatures = e.AudioFeatures,
            Loudness = e.Loudness,
            TruePeak = e.TruePeak,
            DynamicRange = e.DynamicRange,
            IsTrustworthy = e.Integrity != Data.IntegrityLevel.Suspicious && e.Integrity != Data.IntegrityLevel.None,
            QualityConfidence = (e.Bitrate >= 320 || e.Format == "flac") ? 1.0 : 0.5,
            
            // Phase 5: Ultimate Track View
            DropTimestamp = e.DropTimestamp,
            ManualEnergy = e.ManualEnergy,
            SourceProvenance = e.SourceProvenance,
            Rating = e.Rating,
            IsLiked = e.IsLiked,
            PlayCount = e.PlayCount,
            LastPlayedAt = e.LastPlayedAt
        }).ToList();
    }

    public async Task<List<LibraryEntryEntity>> SearchLibraryFtsAsync(string searchTerm, int limit = 100)
    {
        using var context = new AppDbContext();
        var formattedSearch = searchTerm.Trim() + "*";
        
        return await context.LibraryEntries
            .FromSqlRaw("SELECT * FROM LibraryEntries WHERE rowid IN (SELECT rowid FROM LibraryEntriesFts WHERE LibraryEntriesFts MATCH {0})", formattedSearch)
            .Include(le => le.AudioFeatures)
            .AsNoTracking()
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdateAllInstancesMetadataAsync(string trackHash, TrackEnrichmentResult result)
    {
        if (string.IsNullOrEmpty(trackHash) || !result.Success) return;

        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            
            // 1. Update LibraryEntry
            var entry = await context.LibraryEntries.FindAsync(trackHash);
            if (entry != null)
            {
                ApplyMetadata(entry, result);
            }

            // 2. Update Master Track record
            var masterTrack = await context.Tracks.FindAsync(trackHash);
            if (masterTrack != null)
            {
                ApplyMetadata(masterTrack, result);
            }

            // 3. Update all PlaylistTracks
            var tracks = await context.PlaylistTracks
                .Where(t => t.TrackUniqueHash == trackHash)
                .ToListAsync();

            foreach (var t in tracks)
            {
                ApplyMetadata(t, result);
            }

            // 4. Update AudioFeatures if they exist
            var features = await context.AudioFeatures.FirstOrDefaultAsync(af => af.TrackUniqueHash == trackHash);
            if (features != null)
            {
                if (!string.IsNullOrEmpty(result.MusicBrainzId)) features.MusicBrainzId = result.MusicBrainzId;
                if (result.Bpm > 0) features.Bpm = (float)result.Bpm.Value;
                if (result.Energy > 0) features.Energy = (float)result.Energy.Value;
                if (result.Danceability > 0) features.Danceability = (float)result.Danceability.Value;
                if (result.Valence > 0) features.Valence = (float)result.Valence.Value;
                if (!string.IsNullOrEmpty(result.MusicalKey)) features.Key = result.MusicalKey;
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task UpdateLikeStatusAsync(string trackHash, bool isLiked)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();

            // 1. Update LibraryEntry
            var entry = await context.LibraryEntries.FindAsync(trackHash);
            if (entry != null)
            {
                entry.IsLiked = isLiked;
            }

            // 2. Update Master Track record
            var tr = await context.Tracks.FindAsync(trackHash);
            if (tr != null)
            {
                tr.IsLiked = isLiked;
            }

            // 3. Update all PlaylistTracks
            var tracks = await context.PlaylistTracks
                .Where(t => t.TrackUniqueHash == trackHash)
                .ToListAsync();

            foreach (var t in tracks)
            {
                t.IsLiked = isLiked;
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task UpdateRatingAsync(string trackHash, int rating)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();

            // 1. Update LibraryEntry
            var entry = await context.LibraryEntries.FindAsync(trackHash);
            if (entry != null)
            {
                entry.Rating = rating;
            }

            // 2. Update Master Track record
            var tr = await context.Tracks.FindAsync(trackHash);
            if (tr != null)
            {
                tr.Rating = rating;
            }

            // 3. Update all PlaylistTracks
            var tracks = await context.PlaylistTracks
                .Where(t => t.TrackUniqueHash == trackHash)
                .ToListAsync();

            foreach (var t in tracks)
            {
                t.Rating = rating;
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }


    private void ApplyMetadata(object entity, TrackEnrichmentResult result)
    {
        // Reflection-based helper or manual mapping for shared properties
        if (entity is LibraryEntryEntity le)
        {
            // Sync human-readable names if they are currently "Unknown" or filenames
            if (!string.IsNullOrEmpty(result.OfficialArtist) && (le.Artist == "Unknown Artist" || string.IsNullOrWhiteSpace(le.Artist)))
                le.Artist = result.OfficialArtist;
            
            if (!string.IsNullOrEmpty(result.OfficialTitle) && (le.Title == Path.GetFileNameWithoutExtension(le.FilePath) || string.IsNullOrWhiteSpace(le.Title)))
                le.Title = result.OfficialTitle;

            if (!string.IsNullOrEmpty(result.SpotifyId)) le.SpotifyTrackId = result.SpotifyId;
            if (!string.IsNullOrEmpty(result.SpotifyAlbumId)) le.SpotifyAlbumId = result.SpotifyAlbumId;
            if (!string.IsNullOrEmpty(result.SpotifyArtistId)) le.SpotifyArtistId = result.SpotifyArtistId;
            if (!string.IsNullOrEmpty(result.ISRC)) le.ISRC = result.ISRC;
            if (!string.IsNullOrEmpty(result.MusicBrainzId)) le.MusicBrainzId = result.MusicBrainzId;
            if (!string.IsNullOrEmpty(result.AlbumArtUrl)) le.AlbumArtUrl = result.AlbumArtUrl;
            if (result.Bpm > 0) le.BPM = result.Bpm;
            if (result.Energy > 0) le.Energy = result.Energy;
            if (result.Danceability > 0) le.Danceability = result.Danceability;
            if (result.Valence > 0) le.Valence = result.Valence;
            if (!string.IsNullOrEmpty(result.MusicalKey)) le.MusicalKey = result.MusicalKey;
            if (result.Genres?.Any() == true) le.Genres = string.Join(", ", result.Genres);
            if (!string.IsNullOrEmpty(result.DetectedSubGenre)) le.DetectedSubGenre = result.DetectedSubGenre;
            if (result.SubGenreConfidence > 0) le.SubGenreConfidence = result.SubGenreConfidence;
            le.IsEnriched = true;
        }
        else if (entity is PlaylistTrackEntity pt)
        {
            // Sync human-readable names
            if (!string.IsNullOrEmpty(result.OfficialArtist) && (pt.Artist == "Unknown Artist" || string.IsNullOrWhiteSpace(pt.Artist)))
                pt.Artist = result.OfficialArtist;

            if (!string.IsNullOrEmpty(result.OfficialTitle) && string.IsNullOrWhiteSpace(pt.Title))
                pt.Title = result.OfficialTitle;

            if (!string.IsNullOrEmpty(result.SpotifyId)) pt.SpotifyTrackId = result.SpotifyId;
            if (!string.IsNullOrEmpty(result.SpotifyAlbumId)) pt.SpotifyAlbumId = result.SpotifyAlbumId;
            if (!string.IsNullOrEmpty(result.SpotifyArtistId)) pt.SpotifyArtistId = result.SpotifyArtistId;
            if (!string.IsNullOrEmpty(result.ISRC)) pt.ISRC = result.ISRC;
            if (!string.IsNullOrEmpty(result.MusicBrainzId)) pt.MusicBrainzId = result.MusicBrainzId;
            if (!string.IsNullOrEmpty(result.AlbumArtUrl)) pt.AlbumArtUrl = result.AlbumArtUrl;
            if (result.Bpm > 0) pt.BPM = result.Bpm;
            if (result.Energy > 0) pt.Energy = result.Energy;
            if (result.Danceability > 0) pt.Danceability = result.Danceability;
            if (result.Valence > 0) pt.Valence = result.Valence;
            if (!string.IsNullOrEmpty(result.MusicalKey)) pt.MusicalKey = result.MusicalKey;
            if (result.Genres?.Any() == true) pt.Genres = string.Join(", ", result.Genres);
            if (!string.IsNullOrEmpty(result.DetectedSubGenre)) pt.DetectedSubGenre = result.DetectedSubGenre;
            pt.IsEnriched = true;
        }
        else if (entity is TrackEntity tr)
        {
            // Sync human-readable names
            if (!string.IsNullOrEmpty(result.OfficialArtist) && (tr.Artist == "Unknown Artist" || string.IsNullOrWhiteSpace(tr.Artist)))
                tr.Artist = result.OfficialArtist;

            if (!string.IsNullOrEmpty(result.OfficialTitle) && (tr.Title == tr.Filename || string.IsNullOrWhiteSpace(tr.Title)))
                tr.Title = result.OfficialTitle;

            if (!string.IsNullOrEmpty(result.SpotifyId)) tr.SpotifyTrackId = result.SpotifyId;
            if (!string.IsNullOrEmpty(result.SpotifyAlbumId)) tr.SpotifyAlbumId = result.SpotifyAlbumId;
            if (!string.IsNullOrEmpty(result.SpotifyArtistId)) tr.SpotifyArtistId = result.SpotifyArtistId;
            if (!string.IsNullOrEmpty(result.ISRC)) tr.ISRC = result.ISRC;
            if (!string.IsNullOrEmpty(result.MusicBrainzId)) tr.MusicBrainzId = result.MusicBrainzId;
            if (!string.IsNullOrEmpty(result.AlbumArtUrl)) tr.AlbumArtUrl = result.AlbumArtUrl;
            if (result.Bpm > 0) tr.BPM = result.Bpm;
            if (result.Energy > 0) tr.Energy = result.Energy;
            if (result.Danceability > 0) tr.Danceability = result.Danceability;
            if (result.Valence > 0) tr.Valence = result.Valence;
            if (!string.IsNullOrEmpty(result.MusicalKey)) tr.MusicalKey = result.MusicalKey;
            if (result.Genres?.Any() == true) tr.Genres = string.Join(", ", result.Genres);
            if (!string.IsNullOrEmpty(result.DetectedSubGenre)) tr.DetectedSubGenre = result.DetectedSubGenre;
            tr.IsEnriched = true;
        }
    }

    public async Task UpdateAudioFeaturesAsync(AudioFeaturesEntity entity)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var existing = await context.AudioFeatures
                .FirstOrDefaultAsync(f => f.TrackUniqueHash == entity.TrackUniqueHash);

            if (existing == null)
            {
                context.AudioFeatures.Add(entity);
            }
            else
            {
                context.Entry(existing).CurrentValues.SetValues(entity);
            }

            // Sync with LibraryEntry for denormalized fields
            var entry = await context.LibraryEntries.FindAsync(entity.TrackUniqueHash);
            if (entry != null)
            {
                entry.BPM = entity.Bpm;
                entry.Energy = entity.Energy;
                entry.MusicalKey = entity.Key;
                entry.IsEnriched = true;
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<PlaylistTrackEntity>> SearchPlaylistTracksAsync(string query, int limit = 50)
    {
        using var context = new AppDbContext();
        var lowerQuery = query.ToLower();
        return await context.PlaylistTracks
            .Include(t => t.AudioFeatures)
            .Where(t => t.Artist.ToLower().Contains(lowerQuery) || t.Title.ToLower().Contains(lowerQuery))
            .OrderByDescending(t => t.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<PlaylistTrackEntity>> FindTracksInOtherProjectsAsync(
        string artist, string title, Guid excludeProjectId)
    {
        // Read-only hot path: no write semaphore needed.
        // AsNoTracking avoids EF change-tracker overhead.
        using var context = new AppDbContext();
        var artistLower = artist.ToLowerInvariant();
        var titleLower  = title.ToLowerInvariant();

        return await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => t.PlaylistId != excludeProjectId
                     && t.Artist.ToLower() == artistLower
                     && t.Title.ToLower()  == titleLower
                     && t.Status == TrackStatus.Downloaded)
            .ToListAsync();
    }

    public async Task<List<TrackPhraseEntity>> GetPhrasesByHashAsync(string trackHash)
    {
        using var context = new AppDbContext();
        return await context.TrackPhrases
            .AsNoTracking()
            .Where(p => p.TrackUniqueHash == trackHash)
            .OrderBy(p => p.OrderIndex)
            .ToListAsync();
    }

    public async Task SavePhrasesAsync(List<TrackPhraseEntity> phrases)
    {
        if (phrases == null || !phrases.Any()) return;

        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var hash = phrases.First().TrackUniqueHash;

            // Atomic Refresh: Clear existing segments before adding new detection results
            var existing = await context.TrackPhrases.Where(p => p.TrackUniqueHash == hash).ToListAsync();
            context.TrackPhrases.RemoveRange(existing);

            await context.TrackPhrases.AddRangeAsync(phrases);
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }
}
