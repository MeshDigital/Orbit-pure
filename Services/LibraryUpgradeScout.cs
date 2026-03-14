using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;

namespace SLSKDONET.Services;

/// <summary>
/// Service for identifying library upgrade candidates (Self-Healing Library).
/// Scans for low-quality or faked files.
/// </summary>
public class LibraryUpgradeScout
{
    private readonly ILogger<LibraryUpgradeScout> _logger;
    private readonly DatabaseService _databaseService;
    private readonly AppConfig _config;

    public LibraryUpgradeScout(
        ILogger<LibraryUpgradeScout> logger,
        DatabaseService databaseService,
        AppConfig config)
    {
        _logger = logger;
        _databaseService = databaseService;
        _config = config;
    }

    /// <summary>
    /// Scans the library for tracks that could benefit from a quality upgrade.
    /// Returns a list of candidates based on bitrate and trustworthiness.
    /// </summary>
    public async Task<List<TrackEntity>> GetUpgradeCandidatesAsync()
    {
        try
        {
            _logger.LogInformation("Scouting library for upgrade candidates (Bitrate < 320kbps)...");
            
            using var context = new AppDbContext();
            
            // 1. Identify low-quality tracks from the Library
            var bronzeEntries = await context.LibraryEntries
                .Where(e => e.Bitrate < 320 && e.Bitrate > 0)
                .ToListAsync();

            // 2. Filter out those already being processed in the download queue
            var existingQueueIds = await context.Tracks
                .Select(t => t.GlobalId)
                .ToListAsync();

            var candidates = bronzeEntries
                .Where(e => !existingQueueIds.Contains(e.UniqueHash))
                .Select(e => new TrackEntity
                {
                    GlobalId = e.UniqueHash,
                    Artist = e.Artist,
                    Title = e.Title,
                    Bitrate = e.Bitrate,
                    State = "Pending",
                    AddedAt = DateTime.UtcNow,
                    IsTrustworthy = e.Integrity != IntegrityLevel.None
                })
                .ToList();
            
            _logger.LogInformation("Found {Count} potential upgrade candidates.", candidates.Count);
            return candidates;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan library for upgrade candidates.");
            return new List<TrackEntity>();
        }
    }
}
