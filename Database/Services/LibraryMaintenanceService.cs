using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Database.Enums;

namespace SLSKDONET.Database.Services;

/// <summary>
/// Handles two-pass disk and database synchronization, healing and cleaning up download states.
/// </summary>
public sealed class LibraryMaintenanceService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly string _downloadPoolDirectory;
    private readonly ILogger<LibraryMaintenanceService> _logger;

    public LibraryMaintenanceService(
        IDbContextFactory<AppDbContext> contextFactory, 
        string downloadPoolDirectory, 
        ILogger<LibraryMaintenanceService> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _downloadPoolDirectory = downloadPoolDirectory ?? throw new ArgumentNullException(nameof(downloadPoolDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Pass 1: Scans local storage directories to delete abandoned and frozen file writes.
    /// </summary>
    public void PurgeOrphanedPartFiles()
    {
        if (!Directory.Exists(_downloadPoolDirectory))
        {
            _logger.LogWarning($"[LibraryMaintenance] Download pool directory does not exist: {_downloadPoolDirectory}");
            return;
        }

        try
        {
            var partialFiles = Directory.GetFiles(_downloadPoolDirectory, "*.part", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(_downloadPoolDirectory, "*.tmp", SearchOption.AllDirectories));

            foreach (var file in partialFiles)
            {
                var info = new FileInfo(file);
                // If the transient write handle has been inactive for > 12 hours, treat as abandoned
                if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromHours(12))
                {
                    try
                    {
                        info.Delete();
                        _logger.LogInformation($"Successfully purged dead transient file handle: {file}");
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning($"File handle locked by active resource process: {file}. Exception: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Pass 1 of LibraryMaintenance: PurgeOrphanedPartFiles.");
        }
    }

    /// <summary>
    /// Pass 2: Synchronizes physical files with virtual DB states.
    /// </summary>
    public async Task ReconcileLibraryDatabaseAsync()
    {
        try
        {
            using var db = await _contextFactory.CreateDbContextAsync();
            
            // Target records mapped as local assets
            var records = await db.Tracks
                .Where(t => t.IsLocalFile || t.Status == DownloadState.Completed)
                .ToListAsync();

            int resetCount = 0;

            foreach (var track in records)
            {
                // Verify file location integrity
                if (string.IsNullOrEmpty(track.LocalFilePath) || !File.Exists(track.LocalFilePath))
                {
                    track.IsLocalFile = false;
                    track.LocalFilePath = null;
                    track.Status = DownloadState.Pending; // Force return to scheduling search pipeline
                    track.SpectralForensicsData = null;   // Invalidate outdated forensics metadata
                    resetCount++;
                }
            }

            // Heal hung downloads trapped in transient states due to improper closures or crashes
            var transientTracks = await db.Tracks
                .Where(t => t.Status == DownloadState.Downloading)
                .ToListAsync();

            foreach (var track in transientTracks)
            {
                track.Status = DownloadState.Pending;
            }

            await db.SaveChangesAsync();
            _logger.LogInformation($"Synchronization pass complete. Reset {resetCount} missing assets back to Pending status.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Pass 2 of LibraryMaintenance: ReconcileLibraryDatabaseAsync.");
        }
    }
}
