using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Views;

namespace SLSKDONET.Services.AudioAnalysis;

public sealed record TimeTillDropBackfillResult(int Candidates, int Updated, int NoDrop, int Invalid);

/// <summary>
/// One-time background fill for tracks analysed before TimeTillDropSeconds existed. Uses only the
/// stored RMS curve and tempo; no audio decode, cue/grid rewrite, main-drop change or Rekordbox write.
/// A marker prevents a completed pass from running again. New analysis remains the canonical writer.
/// </summary>
public sealed class TimeTillDropBackfillService
{
    private const string PassName = "time-till-drop-v1";
    private const int BatchSize = 64;
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<TimeTillDropBackfillService> _logger;
    private readonly string _markerPath;
    private int _startGate;

    public TimeTillDropBackfillService(
        IDbContextFactory<AppDbContext> dbFactory,
        IEventBus eventBus,
        ILogger<TimeTillDropBackfillService> logger)
        : this(dbFactory, eventBus, logger, DefaultMarkerPath())
    {
    }

    internal TimeTillDropBackfillService(
        IDbContextFactory<AppDbContext> dbFactory,
        IEventBus eventBus,
        ILogger<TimeTillDropBackfillService> logger,
        string markerPath)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _logger = logger;
        _markerPath = markerPath;
    }

    private static string DefaultMarkerPath() =>
        Path.Combine(OrbitPaths.RoamingRoot, "ORBIT", "Backups", $"{PassName}.done");

    /// <summary>Starts after startup database contention has settled. Never throws.</summary>
    public void StartIfPending(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_markerPath) || Interlocked.CompareExchange(ref _startGate, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartDelay, cancellationToken).ConfigureAwait(false);
                await RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // intentional: application shutdown cancels this optional background repair.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TimeTillDropBackfill] Pass failed; it will retry on next launch.");
            }
        }, CancellationToken.None);
    }

    public async Task<TimeTillDropBackfillResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_markerPath)) return new TimeTillDropBackfillResult(0, 0, 0, 0);

        List<string> hashes;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            hashes = await db.AudioFeatures.AsNoTracking()
                .Where(features => features.TimeTillDropSeconds == null &&
                                   features.Bpm > 0 && features.TrackDuration > 0 &&
                                   features.EnergyCurveJson != null &&
                                   features.EnergyCurveJson != "" && features.EnergyCurveJson != "[]")
                .OrderBy(features => features.TrackUniqueHash)
                .Select(features => features.TrackUniqueHash)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        int updated = 0, noDrop = 0, invalid = 0;
        foreach (var batch in hashes.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changedHashes = new List<string>();
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                var rows = await db.AudioFeatures
                    .Where(features => batch.Contains(features.TrackUniqueHash))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var features in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A foreground re-analysis may have populated this after candidate selection.
                    if (features.TimeTillDropSeconds.HasValue) continue;

                    float[] energy;
                    try
                    {
                        energy = JsonSerializer.Deserialize<float[]>(features.EnergyCurveJson) ?? Array.Empty<float>();
                    }
                    catch (JsonException ex)
                    {
                        invalid++;
                        _logger.LogDebug(ex, "[TimeTillDropBackfill] Invalid energy curve for {Hash}", features.TrackUniqueHash);
                        continue;
                    }

                    if (energy.Length == 0)
                    {
                        invalid++;
                        continue;
                    }

                    var result = StructuralAnalysisEngine.Analyze(features.Bpm, features.TrackDuration, energy);
                    if (result.FirstDrop is not { } firstDrop)
                    {
                        noDrop++;
                        continue;
                    }

                    features.TimeTillDropSeconds = (float)firstDrop.TimestampSeconds;
                    changedHashes.Add(features.TrackUniqueHash);
                }

                if (changedHashes.Count > 0)
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            updated += changedHashes.Count;
            foreach (var hash in changedHashes)
                _eventBus.Publish(new TrackAnalysisUpdatedEvent(hash, DateTime.UtcNow));

            if (updated > 0 && updated % 256 < BatchSize)
                await Task.Delay(20, cancellationToken).ConfigureAwait(false); // yield to foreground writers
        }

        WriteMarker($"candidates={hashes.Count}; updated={updated}; noDrop={noDrop}; invalid={invalid}");
        _logger.LogInformation(
            "[TimeTillDropBackfill] Checked {Candidates} tracks: {Updated} updated, {NoDrop} without a usable drop, {Invalid} invalid curves",
            hashes.Count, updated, noDrop, invalid);

        if (updated > 0)
            _eventBus.Publish(new SLSKDONET.Events.LibraryUpdatedEvent("First-drop timing backfill"));

        return new TimeTillDropBackfillResult(hashes.Count, updated, noDrop, invalid);
    }

    private void WriteMarker(string summary)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
        File.WriteAllText(_markerPath, $"{DateTime.UtcNow:O}\n{summary}\n");
    }
}
