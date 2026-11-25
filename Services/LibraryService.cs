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
/// Concrete implementation of ILibraryService.
/// Manages persistent library data (LibraryEntry, PlaylistJob, PlaylistTrack).
/// Now UI-agnostic and focused purely on data management.
/// </summary>
public partial class LibraryService : ILibraryService
{
    private readonly ILogger<LibraryService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly AppConfig _appConfig;
    private readonly IEventBus _eventBus;
    private readonly LibraryCacheService _cache; // Session 1: Performance cache
    private readonly EngineDiagnosticsService? _diagnostics;
    private readonly AudioAnalysis.TrackFingerprintStore? _fingerprintStore;

    // Events now published via IEventBus (ProjectDeletedEvent, ProjectUpdatedEvent)

    private volatile string? _lastTrackQueryError;

    /// <inheritdoc />
    public string? LastTrackQueryError => _lastTrackQueryError;



    public LibraryService(
        ILogger<LibraryService> logger,
        DatabaseService databaseService,
        AppConfig appConfig,
        IEventBus eventBus,
        LibraryCacheService cache, // Session 1: Inject cache
        EngineDiagnosticsService? diagnostics = null,
        AudioAnalysis.TrackFingerprintStore? fingerprintStore = null)
    {
        _logger = logger;
        _databaseService = databaseService;
        _appConfig = appConfig;
        _eventBus = eventBus;
        _cache = cache;
        _diagnostics = diagnostics;
        _fingerprintStore = fingerprintStore;

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

        foreach (var staleEntry in entries.Where(e =>
                     !string.IsNullOrWhiteSpace(e.FilePath) &&
                     !System.IO.File.Exists(e.FilePath)))
        {
            await LogIngestionLifecycleAsync(
                Guid.Empty,
                "ingestion_missing_detected",
                new
                {
                    trackHash = staleEntry.UniqueHash,
                    filePath = staleEntry.FilePath,
                    detectedAtUtc = DateTime.UtcNow,
                    source = "LibraryService.LoadAllLibraryEntriesAsync"
                });

            _eventBus.Publish(new FileMissingDetectedEvent(
                staleEntry.UniqueHash,
                staleEntry.FilePath,
                DateTime.UtcNow,
                "LibraryService.LoadAllLibraryEntriesAsync"));
        }
        
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
                if (!string.IsNullOrWhiteSpace(entry.AudioContentHash))
                    existingEntity.AudioContentHash = entry.AudioContentHash;
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

            // 4. Create and save missing entries.
            // These tracks were already proven absent from LibraryEntries above (existingHashes),
            // so — unlike the general-purpose SaveOrUpdateLibraryEntryAsync — there's no need for
            // each one to redundantly re-check "does this already exist" via its own DB round-trip.
            // Build every entry against one shared context and commit once at the end instead of
            // once per track; on a large backfill (e.g. after a big import) this turns thousands of
            // sequential find+save round-trips into a single batched insert.
            int addedCount = 0;
            using var db = new AppDbContext();
            var pendingEntries = new List<(PlaylistTrackEntity Track, string Hash)>();

            foreach (var track in missingTracks)
            {
                // Basic check to ensure file actually exists before indexing
                if (!System.IO.File.Exists(track.ResolvedFilePath))
                {
                    _logger.LogWarning("Skipping index for missing file: {Path}", track.ResolvedFilePath);
                    await LogIngestionLifecycleAsync(
                        track.PlaylistId,
                        "ingestion_missing_detected",
                        new
                        {
                            trackHash = track.TrackUniqueHash,
                            playlistTrackId = track.Id,
                            filePath = track.ResolvedFilePath,
                            detectedAtUtc = DateTime.UtcNow,
                            source = "LibraryService.SyncLibraryEntriesFromTracksAsync"
                        });

                    _eventBus.Publish(new FileMissingDetectedEvent(
                        track.TrackUniqueHash,
                        track.ResolvedFilePath ?? "",
                        DateTime.UtcNow,
                        "LibraryService.SyncLibraryEntriesFromTracksAsync"));
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

                var hash = string.IsNullOrWhiteSpace(track.TrackUniqueHash)
                    ? track.Id.ToString("N")
                    : track.TrackUniqueHash;

                await LogIngestionLifecycleAsync(
                    track.PlaylistId,
                    "ingestion_started",
                    new
                    {
                        trackHash = hash,
                        playlistTrackId = track.Id,
                        filePath = track.ResolvedFilePath,
                        startedAtUtc = DateTime.UtcNow,
                        source = "LibraryService.SyncLibraryEntriesFromTracksAsync"
                    });

                _eventBus.Publish(new FileIngestionStartedEvent(
                    hash,
                    track.Id,
                    track.ResolvedFilePath,
                    DateTime.UtcNow));

                var entryEntity = LibraryEntryToEntity(entry);
                entryEntity.LastUsedAt = DateTime.UtcNow;
                db.LibraryEntries.Add(entryEntity);
                pendingEntries.Add((track, hash));
                addedCount++;
            }

            if (pendingEntries.Count > 0)
            {
                await db.SaveChangesAsync();
                _cache.InvalidateGlobalLibrary();

                foreach (var (track, hash) in pendingEntries)
                {
                    await LogIngestionLifecycleAsync(
                        track.PlaylistId,
                        "ingestion_completed",
                        new
                        {
                            trackHash = hash,
                            playlistTrackId = track.Id,
                            filePath = track.ResolvedFilePath,
                            completedAtUtc = DateTime.UtcNow,
                            source = "LibraryService.SyncLibraryEntriesFromTracksAsync"
                        });

                    _eventBus.Publish(new FileIngestionCompletedEvent(
                        hash,
                        track.Id,
                        track.ResolvedFilePath ?? "",
                        DateTime.UtcNow));
                }
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
            var hash = string.IsNullOrWhiteSpace(track.TrackUniqueHash)
                ? track.Id.ToString("N")
                : track.TrackUniqueHash;

            await LogIngestionLifecycleAsync(
                track.PlaylistId,
                "ingestion_started",
                new
                {
                    trackHash = hash,
                    playlistTrackId = track.Id,
                    filePath = finalPath,
                    startedAtUtc = DateTime.UtcNow,
                    source = "LibraryService.AddTrackToLibraryIndexAsync"
                });

            _eventBus.Publish(new FileIngestionStartedEvent(
                hash,
                track.Id,
                finalPath,
                DateTime.UtcNow));

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

            await LogIngestionLifecycleAsync(
                track.PlaylistId,
                "ingestion_completed",
                new
                {
                    trackHash = hash,
                    playlistTrackId = track.Id,
                    filePath = finalPath,
                    completedAtUtc = DateTime.UtcNow,
                    source = "LibraryService.AddTrackToLibraryIndexAsync"
                });

            _eventBus.Publish(new FileIngestionCompletedEvent(
                hash,
                track.Id,
                finalPath,
                DateTime.UtcNow));
            
            // Session 2: Invalidate global cache
            _cache.InvalidateGlobalLibrary();

            _logger.LogInformation("Indexed track for All Tracks view: {Title}", track.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index completed track {Title} for All Tracks view", track.Title);
        }
    }

    private async Task LogIngestionLifecycleAsync(Guid playlistId, string action, object details)
    {
        try
        {
            await _databaseService.LogActivityAsync(IngestionActivityLogFactory.Create(playlistId, action, details)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write ingestion activity log {Action}", action);
        }
    }

    public async Task RemoveTrackFromLibraryAsync(string trackHash)
    {
        try
        {
            _logger.LogInformation("Removing track from global library index: {Hash}", trackHash);

            // The master track record (Tracks table) AND the library index row (LibraryEntries —
            // what the Library page's virtualized track list actually queries) are separate
            // tables keyed by the same hash; both have to go or the track keeps showing up in the
            // Library view after a "successful" removal.
            await _databaseService.RemoveTrackAsync(trackHash);
            await _databaseService.DeleteLibraryEntryByHashAsync(trackHash);

            _cache.InvalidateGlobalLibrary();
            // TrackFingerprintStore's in-memory cache is unbounded by design (fingerprints are
            // small), but a deleted track's fingerprint has no reason to stick around — the
            // Invalidate/InvalidateAll API existed but had zero production callers until now.
            _fingerprintStore?.Invalidate(trackHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove track {Hash} from library", trackHash);
        }
    }

    public async Task DeleteLibraryEntryAsync(Guid id)
    {
        try
        {
            _logger.LogInformation("Deleting orphaned library entry: {Id}", id);
            await _databaseService.DeleteLibraryEntryAsync(id);
            _cache.InvalidateGlobalLibrary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete library entry {Id}", id);
            // Rethrow — callers (e.g. OrphanedTrackViewModel.RemoveAsync) decide whether the row
            // is safe to drop from their own UI state, and must not assume success on a swallowed
            // failure. Previously this method ate every exception, so a failed delete still looked
            // like a success to the caller's try/catch, which could never actually fire.
            throw;
        }
    }

}
