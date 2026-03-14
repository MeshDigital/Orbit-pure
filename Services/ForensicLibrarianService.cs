using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public class FraudReport
{
    public string TrackUniqueHash { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    
    public double ExpectedDuration { get; set; }
    public double ActualDuration { get; set; }
    public double DurationDelta => Math.Abs(ExpectedDuration - ActualDuration);
    
    public double? ExpectedBpm { get; set; }
    public double? ActualBpm { get; set; }
    public double BpmDelta => ExpectedBpm.HasValue && ActualBpm.HasValue ? Math.Abs(ExpectedBpm.Value - ActualBpm.Value) : 0;
    
    public bool IsDurationFraud => DurationDelta > 5; // 5 second tolerance
    public bool IsBpmFraud => BpmDelta > 5; // 5 BPM tolerance
}

public class ForensicLibrarianService
{
    private readonly ILibraryService _libraryService;
    private readonly DownloadManager _downloadManager;
    private readonly SLSKDONET.Services.Repositories.ITrackRepository _trackRepository;
    private readonly ILogger<ForensicLibrarianService> _logger;

    public ForensicLibrarianService(
        ILibraryService libraryService,
        DownloadManager downloadManager,
        SLSKDONET.Services.Repositories.ITrackRepository trackRepository,
        ILogger<ForensicLibrarianService> logger)
    {
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _trackRepository = trackRepository;
        _logger = logger;
    }

    /// <summary>
    /// Scans the entire library for tracks where actual and expected metrics mismatch.
    /// </summary>
    public async Task<List<FraudReport>> ScanLibraryForFraudsAsync()
    {
        _logger.LogInformation("Starting global library integrity scan...");
        var entries = await _trackRepository.GetAllLibraryEntriesAsync();
        var frauds = new List<FraudReport>();

        foreach (var entry in entries)
        {
            if (entry.AudioFeatures == null) continue;

            var report = new FraudReport
            {
                TrackUniqueHash = entry.UniqueHash,
                Artist = entry.Artist,
                Title = entry.Title,
                FilePath = entry.FilePath,
                ExpectedDuration = entry.CanonicalDuration ?? 0,
                ActualDuration = entry.AudioFeatures.TrackDuration,
                ExpectedBpm = entry.BPM,
                ActualBpm = entry.AudioFeatures.Bpm
            };

            if (report.IsDurationFraud || report.IsBpmFraud)
            {
                frauds.Add(report);
                _logger.LogWarning("Fraud detected: {Artist} - {Title} (ID: {Hash}). Duration: {Actual}/{Expected}, BPM: {ActBpm}/{ExpBpm}", 
                    entry.Artist, entry.Title, entry.UniqueHash, report.ActualDuration, report.ExpectedDuration, report.ActualBpm, report.ExpectedBpm);
            }
        }

        _logger.LogInformation("Integrity scan complete. Found {Count} potential frauds.", frauds.Count);
        return frauds;
    }

    /// <summary>
    /// Deletes a fraudulent track from disk and re-enqueues it for download.
    /// </summary>
    public async Task<bool> PurgeAndRedownloadAsync(string trackUniqueHash)
    {
        try
        {
            _logger.LogInformation("Executing Purge & Redownload for {Hash}", trackUniqueHash);
            
            var entry = await _libraryService.FindLibraryEntryAsync(trackUniqueHash);
            if (entry == null)
            {
                _logger.LogWarning("Cannot purge {Hash}: Library entry not found.", trackUniqueHash);
                return false;
            }

            // 1. Delete the physical file
            if (!string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath))
            {
                _logger.LogInformation("Deleting physical file: {Path}", entry.FilePath);
                File.Delete(entry.FilePath);
            }

            // 2. Reset the download state in DownloadManager
            // This will also handle DB updates for PlaylistTracks
            await _downloadManager.ResetTrackToPendingAsync(trackUniqueHash);

            // 3. (Optional) Any specific cleanup needed for LibraryEntry?
            // Actually, ResetTrackToPendingAsync should ideally clear the library entry association.
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to purge and redownload track {Hash}", trackUniqueHash);
            return false;
        }
    }
}
