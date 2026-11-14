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
/// Style-score backfill entry point for the wizard: finds tracks whose Discogs style scores are
/// missing <b>or stale</b> and queues low-priority, Mood-only re-analysis requests (the ONNX path
/// only — no Essentia). Stale = <c>StyleModelTag</c> differs from the current model + preprocessing
/// tag (GA10 #255), e.g. every row analysed before the #270 preprocessing fix ("|mel2").
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
    private readonly Lazy<string?> _currentTag;

    /// <param name="currentStyleTag">The tag a fresh analysis would write (tests pass one); by
    /// default it is computed from the bundled model file. Null = only missing styles count.</param>
    public StyleBackfillService(IDbContextFactory<AppDbContext> dbFactory, IEventBus eventBus, ILogger<StyleBackfillService> logger,
        Func<string?>? currentStyleTag = null)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _logger = logger;
        _currentTag = new Lazy<string?>(currentStyleTag ?? DefaultCurrentTag);
    }

    /// <summary>The model + preprocessing tag of a fresh analysis, or null when the model isn't there.</summary>
    public string? CurrentStyleTag => _currentTag.Value;

    private string? DefaultCurrentTag()
    {
        var model = System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "Essentia", "models", "discogs-effnet-bsdynamic-1.onnx");
        try
        {
            // An LFS pointer file is ~130 bytes: no real model, so no "current" tag to compare against.
            return System.IO.File.Exists(model) && new System.IO.FileInfo(model).Length > 1_000_000
                ? SLSKDONET.Services.Similarity.DiscogsEffnetEmbeddingExtractor.BuildModelTag(model)
                : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[StyleBackfill] Could not read the style model to tell stale rows apart; only missing styles count");
            return null;
        }
    }

    public bool IsAvailable => HardwareCapabilities.Allows(HardwareFeature.AiTagging);

    public string UnavailableReason =>
        IsAvailable ? string.Empty : HardwareCapabilities.Current.VerdictFor(HardwareFeature.AiTagging).Reason ?? "AI tagging is switched off";

    /// <summary>
    /// Library tracks whose style scores are missing (no features row, no <c>TopStyle</c>) or stale
    /// (<c>StyleModelTag</c> ≠ <see cref="CurrentStyleTag"/>). One projected query per table.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindTracksNeedingStylesAsync(CancellationToken ct = default)
    {
        var current = CurrentStyleTag;
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var library = await db.LibraryEntries.AsNoTracking().Select(e => e.UniqueHash).ToListAsync(ct).ConfigureAwait(false);
        var fresh = await db.AudioFeatures.AsNoTracking()
            .Where(f => f.TopStyle != null && (current == null || f.StyleModelTag == current))
            .Select(f => f.TrackUniqueHash)
            .ToListAsync(ct).ConfigureAwait(false);

        var freshSet = new HashSet<string>(fresh, StringComparer.Ordinal);
        var needing = library.Where(h => !string.IsNullOrEmpty(h) && !freshSet.Contains(h)).Distinct(StringComparer.Ordinal).ToList();
        _logger.LogInformation("[StyleBackfill] {Needing}/{Total} tracks need style scores (missing or from an older model/preprocessing)",
            needing.Count, library.Count);
        return needing;
    }

    /// <summary>Number of library tracks whose style scores are missing or stale.</summary>
    public async Task<int> CountTracksMissingStylesAsync(CancellationToken ct = default) =>
        (await FindTracksNeedingStylesAsync(ct).ConfigureAwait(false)).Count;
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
