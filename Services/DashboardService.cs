using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Services.Models;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

public record LibraryIntelligenceStats(
    int TotalCount,
    int AnalyzedCount,
    int FlacCount,
    int Mp3HqCount,
    int LowQualityCount,
    Dictionary<string, int> KeyCounts,
    int[] EnergyBuckets);

/// <summary>Rolling-window download outcome summary for the Dashboard's "Last N Days" tile.</summary>
public record DownloadTrendSummary(int Days, int CompletedCount, int FailedCount, double Mp3FallbackPercent)
{
    public int TotalCount => CompletedCount + FailedCount;
}

/// <summary>
/// Aggregates library health metrics for the dashboard/mission control.
/// 
/// WHY: Centralized health tracking provides:
/// 1. User visibility into collection quality (Gold/Silver/Bronze counts)
/// 2. Performance optimization (cached stats vs. live queries every render)
/// 3. Storage management (proactive warning before disk full)
/// 4. Upgrade planning ("200 tracks still at 192kbps - start replacing?")
/// 
/// CACHING STRATEGY:
/// - Stats stored in LibraryHealth table (single row, Id=1)
/// - Recalculated on demand (expensive query) or background worker
/// - UI reads cached value (instant, no DB queries per frame)
/// </summary>
public class DashboardService
{
    public const int DefaultPendingUpdatesRefreshThreshold = 20;

    private readonly ILogger<DashboardService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly AppConfig _config;

    public DashboardService(
        ILogger<DashboardService> logger,
        DatabaseService databaseService,
        AppConfig config)
    {
        _logger = logger;
        _databaseService = databaseService;
        _config = config;
    }

    public async Task<LibraryHealthEntity?> GetLibraryHealthAsync()
    {
        try
        {
            using var context = new AppDbContext();
            // We expect only one record with Id=1
            return await context.LibraryHealth.FindAsync(1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch library health from cache");
            return null;
        }
    }

    public async Task RecalculateLibraryHealthAsync()
    {
        try
        {
            _logger.LogInformation("Recalculating library health statistics...");
            
            using var context = new AppDbContext();
            
            // Query the global LibraryEntries table for high-level health metrics
            var totalTracks = await context.LibraryEntries.CountAsync();
            
            // THREE-TIER QUALITY MODEL:
            // WHY: Reflects real-world audio engineering and user expectations
            
            // GOLD: Lossless formats (FLAC, WAV)
            // - Bit-perfect digital copy (1411 kbps uncompressed equivalent)
            // - No generational loss if re-encoded
            // - Archival quality: 5-10 MB/minute storage cost
            var goldTracks = await context.LibraryEntries.CountAsync(t => t.Format.ToLower() == "flac" || t.Format.ToLower() == "wav");
            
            // SILVER: High-bitrate lossy (320kbps MP3)
            // - "Transparent" encoding: blind tests show <5% can distinguish from lossless
            // - Practical quality: sounds perfect on 99% of systems
            // - Efficient: ~2.5 MB/minute storage
            var silverTracks = await context.LibraryEntries.CountAsync(t => t.Bitrate >= 320 && t.Format.ToLower() != "flac" && t.Format.ToLower() != "wav");
            
            // BRONZE: Acceptable lossy (<320kbps)
            // - Audible compression on critical listening (cymbals, vocals)
            // - Fine for discovery, car audio, background music
            // - Candidates for upgrade hunting
            var bronzeTracks = await context.LibraryEntries.CountAsync(t => t.Bitrate < 320 && t.Bitrate > 0);
            
            // For older pending tracks/upgrades, we can still check PlaylistTracks
            var lowBitratePending = await context.PlaylistTracks.CountAsync(t => t.Status == TrackStatus.Downloaded && t.Bitrate > 0 && t.Bitrate < 256);
            
            // For storage info
            var storageInsight = GetStorageInsight();
            
            var health = await context.LibraryHealth.FindAsync(1) ?? new LibraryHealthEntity { Id = 1 };
            
            health.TotalTracks = totalTracks;
            health.HqTracks = goldTracks + silverTracks; // Anything 320 or Flac
            health.GoldCount = goldTracks;
            health.SilverCount = silverTracks;
            health.BronzeCount = bronzeTracks;
            health.UpgradableCount = lowBitratePending;
            health.TotalStorageBytes = storageInsight.TotalBytes;
            health.FreeStorageBytes = storageInsight.FreeBytes;
            health.LastScanDate = DateTime.Now;
            
            // Calculate top genres (Simplified aggregation)
            var genreCounts = context.PlaylistTracks
                .Where(t => !string.IsNullOrEmpty(t.Genres))
                .AsEnumerable() // Pull into memory for JSON parsing
                .SelectMany(t => (t.Genres ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .GroupBy(g => g)
                .Select(g => new { Genre = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .Take(8)
                .ToList();
                
            health.TopGenresJson = System.Text.Json.JsonSerializer.Serialize(genreCounts);
            // Reset drift marker after a full canonical recalculation pass.
            health.PendingUpdates = 0;

            if (context.Entry(health).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
            {
                context.LibraryHealth.Add(health);
            }
            
            await context.SaveChangesAsync();
            _logger.LogInformation("Library health cache updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recalculate library health");
        }
    }

    public async Task<bool> NeedsLibraryHealthRefreshAsync(
        TimeSpan? maxAge = null,
        int pendingUpdatesThreshold = DefaultPendingUpdatesRefreshThreshold)
    {
        var health = await GetLibraryHealthAsync();
        return ShouldRecalculateLibraryHealth(
            DateTime.Now,
            health?.LastScanDate,
            health?.PendingUpdates ?? 0,
            maxAge ?? TimeSpan.FromMinutes(5),
            pendingUpdatesThreshold);
    }

    public static bool ShouldRecalculateLibraryHealth(
        DateTime now,
        DateTime? lastScanDate,
        int pendingUpdates,
        TimeSpan maxAge,
        int pendingUpdatesThreshold = DefaultPendingUpdatesRefreshThreshold)
    {
        if (lastScanDate is null || lastScanDate <= DateTime.MinValue)
        {
            return true;
        }

        if (pendingUpdates >= Math.Max(1, pendingUpdatesThreshold))
        {
            return true;
        }

        if (maxAge <= TimeSpan.Zero)
        {
            return true;
        }

        return now - lastScanDate.Value >= maxAge;
    }

    public (long TotalBytes, long FreeBytes) GetStorageInsight()
    {
        try
        {
            var path = _config.DownloadDirectory;
            if (string.IsNullOrEmpty(path)) return (0, 0);

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return (0, 0);

            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                return (drive.TotalSize, drive.AvailableFreeSpace);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve storage insights for {Path}", _config.DownloadDirectory);
        }

        return (0, 0);
    }

    public async Task<List<PlaylistJob>> GetRecentPlaylistsAsync(int count = 5)
    {
        try
        {
            // DatabaseService doesn't have a direct "GetRecent" yet, we'll query it here or add to DatabaseService
            // For now, using AppDbContext directly for simplicity in DashboardService
            using var context = new AppDbContext();
            var entities = await context.Projects
                .OrderByDescending(j => j.CreatedAt)
                .Take(count)
                .ToListAsync();
                
            // For better accuracy on dashboard, we can refresh counts from the track table
            // though this might be slower. Let's do it for the recent ones.
            var playlistIds = entities.Select(e => e.Id).ToList();

            // One aggregate query for total/downloaded counts across all playlists, instead of
            // 2 CountAsync round-trips per playlist.
            var countsByPlaylist = await context.PlaylistTracks
                .Where(t => playlistIds.Contains(t.PlaylistId))
                .GroupBy(t => t.PlaylistId)
                .Select(g => new
                {
                    PlaylistId = g.Key,
                    Total = g.Count(),
                    Downloaded = g.Count(t => t.Status == TrackStatus.Downloaded)
                })
                .ToDictionaryAsync(g => g.PlaylistId);

            // Most playlists have no dedicated cover (AlbumArtUrl), only their tracks do — fetch
            // distinct track art URLs for all of them in one round-trip instead of one query per
            // playlist, then take the first few per playlist in memory.
            var missingArtIds = entities.Where(e => string.IsNullOrEmpty(e.AlbumArtUrl)).Select(e => e.Id).ToList();
            var artUrlsByPlaylist = missingArtIds.Count == 0
                ? new Dictionary<Guid, List<string>>()
                : (await context.PlaylistTracks
                    .Where(t => missingArtIds.Contains(t.PlaylistId) && t.AlbumArtUrl != null && t.AlbumArtUrl != "")
                    .Select(t => new { t.PlaylistId, t.AlbumArtUrl })
                    .Distinct()
                    .ToListAsync())
                    .GroupBy(t => t.PlaylistId)
                    .ToDictionary(g => g.Key, g => g.Select(t => t.AlbumArtUrl).Take(4).ToList());

            var models = new List<PlaylistJob>();
            foreach (var entity in entities)
            {
                var model = MapToModel(entity);

                if (countsByPlaylist.TryGetValue(entity.Id, out var counts))
                {
                    model.SuccessfulCount = counts.Downloaded;
                    model.TotalTracks = counts.Total;
                }
                else
                {
                    model.SuccessfulCount = 0;
                    model.TotalTracks = 0;
                }

                if (string.IsNullOrEmpty(model.AlbumArtUrl) && artUrlsByPlaylist.TryGetValue(entity.Id, out var trackArtUrls))
                {
                    model.PlaylistTracks = trackArtUrls
                        .Select(url => new PlaylistTrack { PlaylistId = entity.Id, AlbumArtUrl = url })
                        .ToList();
                }

                models.Add(model);
            }

            return models;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch recent playlists");
            return new List<PlaylistJob>();
        }
    }

    public async Task<List<PlaylistTrack>> GetRecentDownloadedTracksAsync(int count = 8)
    {
        try
        {
            using var context = new AppDbContext();

            var entities = await context.PlaylistTracks
                .AsNoTracking()
                .Where(t => t.Status == TrackStatus.Downloaded && t.CompletedAt != null)
                .OrderByDescending(t => t.CompletedAt)
                .Take(count)
                .ToListAsync();

            return entities.Select(MapToModel).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch recent downloaded tracks");
            return new List<PlaylistTrack>();
        }
    }

    /// <summary>
    /// Aggregates DownloadHistoryEntity (rich per-attempt telemetry already persisted, but
    /// previously only ever read back for per-track history lookups) into a small "Last N Days"
    /// trend for the dashboard: completed/failed counts and how often MP3 fallback was needed.
    /// One GroupBy query, not per-row — same aggregate-query pattern as GetRecentPlaylistsAsync.
    /// </summary>
    public async Task<DownloadTrendSummary> GetDownloadTrendAsync(int days = 7)
    {
        try
        {
            using var context = new AppDbContext();
            var since = DateTime.UtcNow.AddDays(-days);

            var rows = await context.DownloadHistory
                .AsNoTracking()
                .Where(h => h.RecordedAt >= since)
                .GroupBy(h => 1)
                .Select(g => new
                {
                    Completed = g.Count(h => h.FinalState == "Completed"),
                    Failed = g.Count(h => h.FinalState == "Failed"),
                    Total = g.Count(),
                    Mp3FallbackCount = g.Count(h => h.UsedMp3Fallback)
                })
                .FirstOrDefaultAsync();

            if (rows == null || rows.Total == 0)
            {
                return new DownloadTrendSummary(days, 0, 0, 0.0);
            }

            var fallbackPercent = rows.Total > 0 ? (double)rows.Mp3FallbackCount / rows.Total * 100.0 : 0.0;
            return new DownloadTrendSummary(days, rows.Completed, rows.Failed, fallbackPercent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute download trend summary");
            return new DownloadTrendSummary(days, 0, 0, 0.0);
        }
    }

    public async Task<int> GetIncompleteAnalysisTrackCountAsync()
    {
        try
        {
            using var context = new AppDbContext();

            var tracks = await context.PlaylistTracks
                .AsNoTracking()
                .Include(t => t.TechnicalDetails)
                .Include(t => t.AudioFeatures)
                .Where(t => t.Status == TrackStatus.Downloaded)
                .ToListAsync();

            return tracks.Count(track =>
            {
                if (string.IsNullOrWhiteSpace(track.TrackUniqueHash))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(track.ResolvedFilePath) || !File.Exists(track.ResolvedFilePath))
                {
                    return false;
                }

                var hasBpm = (track.BPM ?? 0) > 0;
                var hasKey = !string.IsNullOrWhiteSpace(track.MusicalKey);

                var cueJson = string.IsNullOrWhiteSpace(track.TechnicalDetails?.CuePointsJson)
                    ? track.CuePointsJson
                    : track.TechnicalDetails!.CuePointsJson;
                var hasCues = !string.IsNullOrWhiteSpace(cueJson);

                // Previously checked TechnicalDetails.WaveformData/LowData/MidData/HighData, which
                // were dead columns never actually populated by anything — meaning this was always
                // false and every downloaded track got flagged "incomplete," regardless of real
                // analysis state. The live waveform data is AudioFeaturesEntity.WaveformBlob.
                var hasWaveform = (track.AudioFeatures?.WaveformBlob?.Length ?? 0) > 0;

                return !(hasBpm && hasKey && hasCues && hasWaveform);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute incomplete analysis track count");
            return 0;
        }
    }

    private PlaylistJob MapToModel(PlaylistJobEntity entity)
    {
        return new PlaylistJob
        {
            Id = entity.Id,
            SourceTitle = entity.SourceTitle,
            SourceType = entity.SourceType,
            CreatedAt = entity.CreatedAt,
            TotalTracks = entity.TotalTracks,
            SuccessfulCount = entity.SuccessfulCount,
            FailedCount = entity.FailedCount,
            MissingCount = entity.MissingCount,
            AlbumArtUrl = entity.AlbumArtUrl,
            SourceUrl = entity.SourceUrl,
            PlaylistTracks = new List<PlaylistTrack>() // Empty list for dashboard display
        };
    }

    public async Task<LibraryIntelligenceStats> GetLibraryIntelligenceStatsAsync()
    {
        try
        {
            using var context = new AppDbContext();

            var totalCount = await context.LibraryEntries.CountAsync();

            // AudioFeatures rows for tracks that have since been removed/replaced are never
            // cleaned up on delete (no cascading delete configured), so filtering to only rows
            // whose hash still exists in LibraryEntries keeps "analyzed" from exceeding the
            // actual current library size (it used to: 3167 analyzed vs. 1424 real tracks).
            var liveFeatures = context.AudioFeatures
                .Where(f => context.LibraryEntries.Any(le => le.UniqueHash == f.TrackUniqueHash));

            var analyzedCount = await liveFeatures.CountAsync(f => f.Bpm > 0);

            var flacCount = await context.LibraryEntries.CountAsync(e =>
                e.Format != null && (e.Format.ToUpper() == "FLAC" || e.Format.ToUpper() == "WAV" || e.Format.ToUpper() == "AIFF"));
            var mp3HqCount = await context.LibraryEntries.CountAsync(e =>
                e.Bitrate >= 300 && e.Format != null && e.Format.ToUpper() != "FLAC" && e.Format.ToUpper() != "WAV" && e.Format.ToUpper() != "AIFF");
            var lowQualityCount = Math.Max(0, totalCount - flacCount - mp3HqCount);

            var keyCounts = await liveFeatures
                .Where(f => f.CamelotKey != null && f.CamelotKey != string.Empty)
                .GroupBy(f => f.CamelotKey)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .ToListAsync();

            var energyValues = await liveFeatures
                .Where(f => f.Bpm > 0 && f.Energy > 0)
                .Select(f => (double)f.Energy)
                .ToListAsync();

            var buckets = new int[5];
            foreach (var e in energyValues)
            {
                int b = Math.Min(4, (int)(e * 5));
                buckets[b]++;
            }

            return new LibraryIntelligenceStats(
                totalCount, analyzedCount, flacCount, mp3HqCount, lowQualityCount,
                keyCounts.ToDictionary(k => k.Key, k => k.Count),
                buckets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load library intelligence stats");
            return new LibraryIntelligenceStats(0, 0, 0, 0, 0, new Dictionary<string, int>(), new int[5]);
        }
    }

    private static PlaylistTrack MapToModel(PlaylistTrackEntity entity)
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
            Bitrate = entity.Bitrate,
            Format = entity.Format,
            AddedAt = entity.AddedAt,
            CompletedAt = entity.CompletedAt,
            SpotifyTrackId = entity.SpotifyTrackId,
            ISRC = entity.ISRC,
            MusicBrainzId = entity.MusicBrainzId,
            SpotifyAlbumId = entity.SpotifyAlbumId,
            SpotifyArtistId = entity.SpotifyArtistId,
            AlbumArtUrl = entity.AlbumArtUrl,
            ArtistImageUrl = entity.ArtistImageUrl,
            Genres = entity.Genres,
            Popularity = entity.Popularity,
            CanonicalDuration = entity.CanonicalDuration,
            ReleaseDate = entity.ReleaseDate,
            Label = entity.Label,
            Comments = entity.Comments,
            MusicalKey = entity.MusicalKey,
            BPM = entity.BPM,
            CuePointsJson = entity.CuePointsJson,
            AudioFingerprint = entity.AudioFingerprint,
            BitrateScore = entity.BitrateScore,
            AnalysisOffset = entity.AnalysisOffset,
            Energy = entity.Energy,
            Danceability = entity.Danceability,
            Valence = entity.Valence,
            SpotifyBPM = entity.SpotifyBPM,
            SpotifyKey = entity.SpotifyKey,
            ManualBPM = entity.ManualBPM,
            ManualKey = entity.ManualKey,
            SpectralHash = entity.SpectralHash,
            QualityConfidence = entity.QualityConfidence,
            FrequencyCutoff = entity.FrequencyCutoff,
            IsTrustworthy = entity.IsTrustworthy,
            Integrity = entity.Integrity,
            QualityDetails = entity.QualityDetails,
            Loudness = entity.Loudness,
            TruePeak = entity.TruePeak,
            DynamicRange = entity.DynamicRange,
            Priority = entity.Priority,
            SourcePlaylistId = entity.SourcePlaylistId,
            SourcePlaylistName = entity.SourcePlaylistName,
            IsEnriched = entity.IsEnriched,
            IsUserPaused = entity.IsUserPaused,
            StalledReason = entity.StalledReason,
            IsClearedFromDownloadCenter = entity.IsClearedFromDownloadCenter,
            IsPrepared = entity.IsPrepared,
            MoodTag = entity.MoodTag,
            DetectedSubGenre = entity.DetectedSubGenre,
            SubGenreConfidence = entity.SubGenreConfidence,
            PrimaryGenre = entity.PrimaryGenre,
            InstrumentalProbability = entity.InstrumentalProbability,
            Arousal = entity.Arousal,
            IsDjTool = entity.IsDjTool,
            PreferredFormats = entity.PreferredFormats,
            MinBitrateOverride = entity.MinBitrateOverride,
            DropTimestamp = entity.DropTimestamp,
            ManualEnergy = entity.ManualEnergy,
            SourceProvenance = entity.SourceProvenance,
            SearchRetryCount = entity.SearchRetryCount,
            NotFoundRestartCount = entity.NotFoundRestartCount,
            Rating = entity.Rating,
            IsLiked = entity.IsLiked,
            PlayCount = entity.PlayCount,
            LastPlayedAt = entity.LastPlayedAt
        };
    }
}
