using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Platform;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>
/// Style-score backfill entry point for the wizard: counts tracks missing their Discogs style
/// scores and queues low-priority, Mood-only re-analysis requests (the ONNX path only — no Essentia).
/// Unavailable when <see cref="HardwareFeature.AiTagging"/> is off, and reports why instead of
/// failing silently.
/// </summary>
public sealed class StyleBackfillService
{
    /// <summary>Rough per-track cost, refined from real measurements before release (P3 asks for the number in the PR).</summary>
    public static readonly TimeSpan EstimatedPerTrack = TimeSpan.FromSeconds(2);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<StyleBackfillService> _logger;

    public StyleBackfillService(IDbContextFactory<AppDbContext> dbFactory, IEventBus eventBus, ILogger<StyleBackfillService> logger)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _logger = logger;
    }

    public bool IsAvailable => HardwareCapabilities.Allows(HardwareFeature.AiTagging);

    public string UnavailableReason =>
        IsAvailable ? string.Empty : HardwareCapabilities.Current.VerdictFor(HardwareFeature.AiTagging).Reason ?? "AI tagging is switched off";

    /// <summary>Number of library tracks that have no persisted <see cref="TopStyle"/>.</summary>
    public async Task<int> CountTracksMissingStylesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var styled = await db.AudioFeatures
            .AsNoTracking()
            .Where(f => f.TopStyle != null)
            .Select(f => f.TrackUniqueHash)
            .Distinct()
            .CountAsync(ct).ConfigureAwait(false);

        var total = await db.LibraryEntries.AsNoTracking().CountAsync(ct).ConfigureAwait(false);
        var missing = Math.Max(0, total - styled);
        _logger.LogInformation("[StyleBackfill] {Missing}/{Total} tracks missing style scores", missing, total);
        return missing;
    }

    /// <summary>
    /// Queues the given hashes for a Mood-only analysis (ONNX path, no Essentia). Returns null when
    /// queued, or a human-readable reason when unavailable.
    /// </summary>
    public async Task<string?> QueueStyleBackfillAsync(IEnumerable<string> hashes, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            _logger.LogWarning("[StyleBackfill] Not available: {Reason}", UnavailableReason);
            return UnavailableReason;
        }

        var list = hashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var hash in list)
            _eventBus.Publish(new TrackAnalysisRequestedEvent(hash, Engines: AnalysisEngine.Mood));

        _logger.LogInformation("[StyleBackfill] Queued {Count} tracks for Mood-only style backfill", list.Count);
        await Task.CompletedTask.ConfigureAwait(false);
        return null;
    }
}
