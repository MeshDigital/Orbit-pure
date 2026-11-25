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


/// <summary>
/// WAL/index optimization, genre enrichment, backups, audio features, and chat history (#307).
/// </summary>
public partial class DatabaseService
{
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

    /// <summary>The track's library genre and who wrote it, for metadata-first genre resolution (GA8 #253).
    /// Null when the track has no genre; a failed read is logged and treated as "no library genre".</summary>
    public async Task<Engine.Analysis.GenreFusion.TrackGenreContext?> GetTrackGenreContextAsync(string uniqueHash, CancellationToken ct = default)
    {
        try
        {
            await using var context = new AppDbContext();
            return await AudioAnalysis.TrackGenreContextQuery.LoadAsync(context, uniqueHash, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[DatabaseService] Couldn't read the library genre for {Hash}; using the audio genre", uniqueHash);
            return null;
        }
    }

    /// <summary>
    /// Returns the local file path for a track by its unique hash.
    /// Checks LibraryEntries.FilePath first, then Tracks.LocalFilePath.
    /// Returns null if no path is found or the file no longer exists on disk.
    /// </summary>
    public async Task<string?> GetLocalFilePathByHashAsync(string uniqueHash)
    {
        using var context = new AppDbContext();

        var libPath = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.UniqueHash == uniqueHash && e.FilePath != null && e.FilePath != "")
            .Select(e => e.FilePath)
            .FirstOrDefaultAsync();

        if (!string.IsNullOrEmpty(libPath) && System.IO.File.Exists(libPath))
            return libPath;

        var trackPath = await context.Tracks
            .AsNoTracking()
            .Where(t => t.GlobalId == uniqueHash && t.LocalFilePath != null && t.LocalFilePath != "")
            .Select(t => t.LocalFilePath)
            .FirstOrDefaultAsync();

        if (!string.IsNullOrEmpty(trackPath) && System.IO.File.Exists(trackPath))
            return trackPath;

        return null;
    }

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingMusicBrainzEnrichmentAsync(int count)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Where(e => !string.IsNullOrEmpty(e.ISRC) && string.IsNullOrEmpty(e.MusicBrainzId))
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// Returns library entries that have no MusicBrainz data and no ISRC either,
    /// but have enough artist/title metadata to attempt a name-based search.
    /// </summary>
    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingMusicBrainzEnrichmentByNameAsync(int count)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Where(e =>
                string.IsNullOrEmpty(e.MusicBrainzId) &&
                string.IsNullOrEmpty(e.ISRC) &&
                !string.IsNullOrEmpty(e.Artist) &&
                e.Artist != "Unknown Artist" &&
                !string.IsNullOrEmpty(e.Title))
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

            // Sync denormalized fields
            await SyncDenormalizedFeaturesAsync(context, features).ConfigureAwait(false);

            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Atomically applies a beat-grid edit: updates <see cref="AudioFeaturesEntity.Bpm"/>,
    /// <see cref="AudioFeaturesEntity.BeatGridJson"/> and <see cref="AudioFeaturesEntity.DownbeatOffsetSeconds"/>
    /// on the audio_features row, and sets BPM + ManualBPM on every LibraryEntries, Tracks and
    /// PlaylistTracks row for the hash — so the grid edit, the analysis BPM and the displayed library
    /// BPM can never disagree. TagBPM is left alone (the file tag is not a grid edit). Returns false
    /// when the track has no audio_features row.
    /// </summary>
    public async Task<bool> ApplyGridEditAsync(AudioFeaturesEntity features, CancellationToken ct = default)
    {
        await _writeSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            var existing = await context.AudioFeatures
                .FirstOrDefaultAsync(f => f.TrackUniqueHash == features.TrackUniqueHash, ct)
                .ConfigureAwait(false);
            if (existing is null) return false;

            features.Id = existing.Id;
            context.Entry(existing).CurrentValues.SetValues(features);

            double bpm = features.Bpm;
            string hash = features.TrackUniqueHash;

            foreach (var row in await context.PlaylistTracks
                         .Where(t => t.TrackUniqueHash == hash)
                         .ToListAsync(ct).ConfigureAwait(false))
            {
                row.BPM = bpm;
                row.ManualBPM = bpm;
            }
            foreach (var row in await context.LibraryEntries
                         .Where(t => t.UniqueHash == hash)
                         .ToListAsync(ct).ConfigureAwait(false))
            {
                row.BPM = bpm;
                row.ManualBPM = bpm;
            }
            foreach (var row in await context.Tracks
                         .Where(t => t.GlobalId == hash)
                         .ToListAsync(ct).ConfigureAwait(false))
            {
                row.BPM = bpm;
                row.ManualBPM = bpm;
            }

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// After a <b>tempo</b> edit (×2/÷2, typed BPM, reset): keeps every phrase where it is in the music
    /// and re-numbers its bars on the new grid (issue #225). Keeping the bar numbers instead would move
    /// every boundary by 1/factor in time. Phrases that are not bar-indexed (pre-migration) are left alone.
    /// </summary>
    public async Task ReindexPhraseBarsFromSecondsAsync(string trackHash, double bpm, double downbeat, CancellationToken ct = default)
    {
        if (bpm <= 0) return;
        await _writeSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            var phrases = await context.TrackPhrases
                .Where(p => p.TrackUniqueHash == trackHash)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var p in phrases)
            {
                if (p.BarCount > 0 || p.StartBar > 0)
                {
                    int startBar = AudioAnalysis.PhraseBarIndexing.SecondsToBar(p.StartTimeSeconds, bpm, downbeat);
                    int endBar = AudioAnalysis.PhraseBarIndexing.SecondsToBar(p.EndTimeSeconds, bpm, downbeat);
                    p.StartBar = startBar;
                    p.BarCount = Math.Max(1, endBar - startBar);
                }
            }
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// Recomputes the seconds columns of a track's bar-indexed phrases from the (new) grid after a
    /// <b>phase</b> edit (slip, Bar 1), so phrase boundaries follow the bars (issue #225). Phrases that are not bar-indexed
    /// (<c>BarCount == 0</c>, pre-migration) are left untouched.
    /// </summary>
    public async Task ReindexPhraseSecondsFromBarsAsync(string trackHash, double bpm, double downbeat, CancellationToken ct = default)
    {
        if (bpm <= 0) return;
        await _writeSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            var phrases = await context.TrackPhrases
                .Where(p => p.TrackUniqueHash == trackHash)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var p in phrases)
            {
                if (p.BarCount > 0 || p.StartBar > 0)
                {
                    p.StartTimeSeconds = (float)AudioAnalysis.PhraseBarIndexing.BarToSeconds(p.StartBar, bpm, downbeat);
                    p.EndTimeSeconds = (float)AudioAnalysis.PhraseBarIndexing.BarToSeconds(p.StartBar + p.BarCount, bpm, downbeat);
                }
            }
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
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

    /// <summary>
    /// Writes a consistent snapshot of the library to <paramref name="backupPath"/> (or a
    /// timestamped file in the Backups folder when null) and returns its path.
    /// Uses SQLite's online backup API: a plain file copy of a WAL-mode database can miss
    /// committed pages still in the -wal file. Throws on failure so the caller can report it —
    /// it used to log and return as if it had succeeded.
    /// </summary>
    public async Task<string> BackupDatabaseAsync(string? backupPath = null)
    {
        return await Task.Run(async () =>
        {
            var path = await SLSKDONET.Services.LibraryDatabaseMaintenance.CreateBackupAsync(targetPath: backupPath).ConfigureAwait(false);
            _logger.LogInformation("Database backup created at {Path}", path);
            return path;
        }).ConfigureAwait(false);
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

    public List<PeerReliabilityEntity> GetPeerReliabilityStats()
    {
        using var context = new AppDbContext();
        return context.PeerReliability.ToList();
    }

    public void UpsertPeerReliability(PeerReliabilityEntity entity)
    {
        using var context = new AppDbContext();
        var existing = context.PeerReliability.Find(entity.Username);
        if (existing != null)
        {
            context.Entry(existing).CurrentValues.SetValues(entity);
        }
        else
        {
            context.PeerReliability.Add(entity);
        }
        context.SaveChanges();
    }

    // ── Download History ──────────────────────────────────────────────────────

    /// <summary>
    /// Persists a single completed or failed download attempt to the DownloadHistory table.
    /// Called fire-and-forget from UnifiedTrackViewModel on first terminal-state transition.
    /// </summary>
    public async Task RecordDownloadHistoryAsync(Data.Entities.DownloadHistoryEntity entity)
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            context.DownloadHistory.Add(entity);
            await context.SaveChangesAsync().ConfigureAwait(false);
            _logger.LogDebug("[DownloadHistory] Recorded {State} for {Artist} - {Title} (peer: {Peer})",
                entity.FinalState, entity.Artist, entity.Title, entity.PeerUsername ?? "(none)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DownloadHistory] Failed to record download history for {TrackHash}", entity.TrackHash);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// Returns the most recent download history entries, newest first.
    /// </summary>
    public async Task<List<Data.Entities.DownloadHistoryEntity>> GetRecentDownloadHistoryAsync(int limit = 500)
    {
        using var context = new AppDbContext();
        return await context.DownloadHistory
            .OrderByDescending(x => x.RecordedAt)
            .Take(limit)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns download history for a specific track hash.
    /// </summary>
    public async Task<List<Data.Entities.DownloadHistoryEntity>> GetDownloadHistoryForTrackAsync(string trackHash)
    {
        using var context = new AppDbContext();
        return await context.DownloadHistory
            .Where(x => x.TrackHash == trackHash)
            .OrderByDescending(x => x.RecordedAt)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    private async Task SyncDenormalizedFeaturesAsync(AppDbContext context, AudioFeaturesEntity features)
    {
        var trackGlobalId = features.TrackUniqueHash;

        var playlistRows = await context.PlaylistTracks
            .Where(t => t.TrackUniqueHash == trackGlobalId)
            .ToListAsync()
            .ConfigureAwait(false);
        foreach (var row in playlistRows)
        {
            // Analysis fills BPM only when nothing more authoritative is already set — a manual
            // edit or a file-embedded tag both outrank Essentia, which has a confirmed quantization
            // bias on breakbeat/DNB content (see TagBPM's doc comment). Every other analysis field
            // below is untouched; only BPM was diagnosed as wrong.
            if (row.ManualBPM is null && row.TagBPM is null) row.BPM = features.Bpm;
            row.MusicalKey = features.Key;
            row.Energy = features.Energy;
            row.Danceability = features.Danceability;
            row.Valence = features.Valence;
            row.Arousal = features.Arousal;
            row.IsDjTool = features.IsDjTool;
            row.Loudness = SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(features.LoudnessLUFS);
            row.CuePointsJson = features.CuePointsJson;
            row.MoodTag = features.MoodTag;
            row.DetectedSubGenre = features.DetectedSubGenre;
            row.SubGenreConfidence = features.SubGenreConfidence;
            row.InstrumentalProbability = features.InstrumentalProbability;
            row.DropTimestamp = features.DropTimeSeconds;
        }

        var libraryRows = await context.LibraryEntries
            .Where(t => t.UniqueHash == trackGlobalId)
            .ToListAsync()
            .ConfigureAwait(false);
        foreach (var row in libraryRows)
        {
            // Analysis fills BPM only when nothing more authoritative is already set — a manual
            // edit or a file-embedded tag both outrank Essentia, which has a confirmed quantization
            // bias on breakbeat/DNB content (see TagBPM's doc comment). Every other analysis field
            // below is untouched; only BPM was diagnosed as wrong.
            if (row.ManualBPM is null && row.TagBPM is null) row.BPM = features.Bpm;
            row.MusicalKey = features.Key;
            row.Energy = features.Energy;
            row.Danceability = features.Danceability;
            row.Valence = features.Valence;
            row.Arousal = features.Arousal;
            row.IsDjTool = features.IsDjTool;
            row.Loudness = SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(features.LoudnessLUFS);
            row.CuePointsJson = features.CuePointsJson;
            row.MoodTag = features.MoodTag;
            row.DetectedSubGenre = features.DetectedSubGenre;
            row.SubGenreConfidence = features.SubGenreConfidence;
            row.InstrumentalProbability = features.InstrumentalProbability;
            row.DropTimestamp = features.DropTimeSeconds;
        }

        var masterTracks = await context.Tracks
            .Where(t => t.GlobalId == trackGlobalId)
            .ToListAsync()
            .ConfigureAwait(false);
        foreach (var row in masterTracks)
        {
            // Analysis fills BPM only when nothing more authoritative is already set — a manual
            // edit or a file-embedded tag both outrank Essentia, which has a confirmed quantization
            // bias on breakbeat/DNB content (see TagBPM's doc comment). Every other analysis field
            // below is untouched; only BPM was diagnosed as wrong.
            if (row.ManualBPM is null && row.TagBPM is null) row.BPM = features.Bpm;
            row.MusicalKey = features.Key;
            row.Energy = features.Energy;
            row.Danceability = features.Danceability;
            row.Valence = features.Valence;
            row.CuePointsJson = features.CuePointsJson;
            row.MoodTag = features.MoodTag;
            row.DetectedSubGenre = features.DetectedSubGenre;
            row.SubGenreConfidence = features.SubGenreConfidence;
            row.InstrumentalProbability = features.InstrumentalProbability;
            row.DropTimestamp = features.DropTimeSeconds;
        }
    }

    // ── Social: per-user download history ──────────────────────────────────────

    /// <summary>Download history entries for a specific Soulseek peer, newest first.</summary>
    public async Task<List<Data.Entities.DownloadHistoryEntity>> GetDownloadHistoryForUserAsync(string username, int limit = 200)
    {
        using var context = new AppDbContext();
        return await context.DownloadHistory
            .Where(x => x.PeerUsername == username)
            .OrderByDescending(x => x.RecordedAt)
            .Take(limit)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Per-peer download counts/last-seen, grouped from DownloadHistory — the row source
    /// for the Users/Contacts page. Cached for <see cref="DownloadedUsersSummaryCacheTtl"/> since
    /// this GroupBy can be a meaningful scan over a large table and the Refresh button re-triggers
    /// it on demand.</summary>
    public async Task<List<UserDownloadSummary>> GetDownloadedUsersSummaryAsync()
    {
        var cached = _downloadedUsersSummaryCache;
        if (cached != null && DateTime.UtcNow - _downloadedUsersSummaryCachedAtUtc < DownloadedUsersSummaryCacheTtl)
            return cached;

        using var context = new AppDbContext();
        var result = await context.DownloadHistory
            .Where(x => x.PeerUsername != null)
            .GroupBy(x => x.PeerUsername!)
            .Select(g => new UserDownloadSummary(
                g.Key,
                g.Count(),
                g.Count(x => x.FinalState == "Completed"),
                g.Max(x => x.RecordedAt)))
            .ToListAsync()
            .ConfigureAwait(false);

        _downloadedUsersSummaryCache = result;
        _downloadedUsersSummaryCachedAtUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    /// The most recent peer who successfully delivered this exact track — used by
    /// DownloadDiscoveryService to give a proven-source ranking bonus on redownload.
    /// </summary>
    public async Task<string?> GetLastSuccessfulPeerForTrackAsync(string trackHash)
    {
        using var context = new AppDbContext();
        return await context.DownloadHistory
            .Where(x => x.TrackHash == trackHash && x.FinalState == "Completed" && x.PeerUsername != null)
            .OrderByDescending(x => x.RecordedAt)
            .Select(x => x.PeerUsername)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    // ── Social: 1:1 private messages ─────────────────────────────────────────────

    /// <summary>
    /// Persists a private message. Dedupes incoming (replayed) messages via the unique index on
    /// SoulseekMessageId — a re-insert throws a constraint violation, treated as a no-op.
    /// </summary>
    public async Task RecordPrivateMessageAsync(Data.Entities.PrivateMessageEntity entity)
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            context.PrivateMessages.Add(entity);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogDebug(ex, "[PrivateMessage] Duplicate/replayed message {SoulseekMessageId} from {Peer} ignored", entity.SoulseekMessageId, entity.PeerUsername);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PrivateMessage] Failed to record message from {Peer}", entity.PeerUsername);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// A page of the conversation with one peer, oldest-first (ready to render top-to-bottom).
    /// Pass <paramref name="beforeUtc"/> to fetch the page immediately preceding an already-loaded
    /// message, for "load earlier messages" pagination.
    /// </summary>
    public async Task<List<Data.Entities.PrivateMessageEntity>> GetConversationAsync(string peerUsername, int limit = 500, DateTime? beforeUtc = null)
    {
        using var context = new AppDbContext();
        var query = context.PrivateMessages.Where(x => x.PeerUsername == peerUsername);
        if (beforeUtc.HasValue)
            query = query.Where(x => x.TimestampUtc < beforeUtc.Value);

        return await query
            .OrderByDescending(x => x.TimestampUtc)
            .Take(limit)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Removes one message from local history — local-only, the Soulseek protocol has no message recall.</summary>
    public async Task DeletePrivateMessageAsync(Guid id)
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            await context.PrivateMessages.Where(x => x.Id == id).ExecuteDeleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>Wipes an entire 1:1 conversation's local history — e.g. for a spam/gate-bot peer you just want gone.</summary>
    public async Task DeleteConversationAsync(string peerUsername)
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            await context.PrivateMessages.Where(x => x.PeerUsername == peerUsername).ExecuteDeleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// Recent 1:1 conversations with a last-message preview, most recently active first — the data
    /// source for the Users page's "Conversations" section (distinct from the full "everyone you've
    /// downloaded from" row list). Grouped client-side: private message history is small enough that
    /// pulling the last ~2000 rows and grouping in memory is simpler than a database-specific
    /// "greatest-n-per-group" query, and avoids EF translation issues with First() inside GroupBy.
    /// </summary>
    public async Task<List<ConversationSummary>> GetRecentConversationsAsync()
    {
        using var context = new AppDbContext();
        var recentMessages = await context.PrivateMessages
            .OrderByDescending(x => x.TimestampUtc)
            .Take(2000)
            .ToListAsync()
            .ConfigureAwait(false);

        return recentMessages
            .GroupBy(x => x.PeerUsername, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Latest: g.First(), HasUnread: g.Any(m => !m.IsOutgoing && !m.IsRead))) // already ordered by TimestampUtc desc, so first = most recent per peer
            .OrderByDescending(x => x.Latest.TimestampUtc)
            .Select(x => new ConversationSummary(x.Latest.PeerUsername, x.Latest.Message, x.Latest.TimestampUtc, x.Latest.IsOutgoing, x.HasUnread))
            .ToList();
    }

    /// <summary>Marks every message in a 1:1 conversation as read — called when its Chat tab is opened.</summary>
    public async Task MarkConversationReadAsync(string peerUsername)
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            await context.PrivateMessages
                .Where(x => x.PeerUsername == peerUsername && !x.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRead, true))
                .ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    // ── Social: chat rooms ────────────────────────────────────────────────────────

    public async Task RecordRoomMessageAsync(Data.Entities.RoomMessageEntity entity)
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            context.RoomMessages.Add(entity);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoomMessage] Failed to record message in {RoomName}", entity.RoomName);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// A page of message history for one room, oldest-first (ready to render top-to-bottom).
    /// Pass <paramref name="beforeUtc"/> for "load earlier messages" pagination.
    /// </summary>
    public async Task<List<Data.Entities.RoomMessageEntity>> GetRoomHistoryAsync(string roomName, int limit = 500, DateTime? beforeUtc = null)
    {
        using var context = new AppDbContext();
        var query = context.RoomMessages.Where(x => x.RoomName == roomName);
        if (beforeUtc.HasValue)
            query = query.Where(x => x.TimestampUtc < beforeUtc.Value);

        return await query
            .OrderByDescending(x => x.TimestampUtc)
            .Take(limit)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Marks every message in a room as read — called when the room is opened/selected.</summary>
    public async Task MarkRoomReadAsync(string roomName)
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            await context.RoomMessages
                .Where(x => x.RoomName == roomName && !x.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRead, true))
                .ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>Wipes an entire room's local history on this device — other members' own copies are unaffected.</summary>
    public async Task DeleteRoomHistoryAsync(string roomName)
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            await context.RoomMessages.Where(x => x.RoomName == roomName).ExecuteDeleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>Removes one message from local room history — local-only, the Soulseek protocol has no message recall.</summary>
    public async Task DeleteRoomMessageAsync(Guid id)
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var context = new AppDbContext();
            await context.RoomMessages.Where(x => x.Id == id).ExecuteDeleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }
}
