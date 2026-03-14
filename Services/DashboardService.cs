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
                .Take(5)
                .ToList();
                
            health.TopGenresJson = System.Text.Json.JsonSerializer.Serialize(genreCounts);

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
            var models = new List<PlaylistJob>();
            foreach (var entity in entities)
            {
                var model = MapToModel(entity);
                // Dynamically fetch counts to ensure dashboard is 100% accurate
                model.SuccessfulCount = await context.PlaylistTracks.CountAsync(t => t.PlaylistId == entity.Id && t.Status == TrackStatus.Downloaded);
                model.TotalTracks = await context.PlaylistTracks.CountAsync(t => t.PlaylistId == entity.Id);
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
}
