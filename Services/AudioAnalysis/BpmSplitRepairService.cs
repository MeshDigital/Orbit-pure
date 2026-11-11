using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Views;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// One-time repair of "split-brain" BPMs left by the pre-#220 Cue Forge (issue #220): ×2/÷2 and a
/// typed BPM used to write only the library <c>ManualBPM</c>, never <c>audio_features.Bpm</c> or the
/// beat list, so the grid, transition planning and cue generation kept the old tempo. Each track whose
/// <c>ManualBPM</c> differs from the grid BPM by more than 0.5 is re-applied through
/// <see cref="IBeatGridEditService"/>: a ratio of ~2 or ~0.5 as ×2/÷2 (the beat list is rescaled),
/// anything else as a typed BPM. <c>TagBPM</c> is never used — a file tag is not a grid edit.
/// Runs once (marker file); a consistent SQLite backup is not needed because every write goes through
/// the edit service's own single-transaction path and is reversible by the user (Reset / Use suggestion).
/// </summary>
public sealed class BpmSplitRepairService
{
    private const string PassName = "bpm-split-repair-v1";
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(90);

    private readonly ILogger<BpmSplitRepairService> _logger;
    private readonly IBeatGridEditService _gridEdit;
    private readonly IEventBus _eventBus;

    public BpmSplitRepairService(ILogger<BpmSplitRepairService> logger, IBeatGridEditService gridEdit, IEventBus eventBus)
    {
        _logger = logger;
        _gridEdit = gridEdit;
        _eventBus = eventBus;
    }

    /// <summary>Where the "done" marker lives. Overridable in tests.</summary>
    internal string DataDirectory { get; set; } = Path.Combine(OrbitPaths.RoamingRoot, "ORBIT", "Backups");

    private string MarkerPath => Path.Combine(DataDirectory, $"{PassName}.done");

    public void StartIfPending(CancellationToken ct = default)
    {
        if (File.Exists(MarkerPath)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartDelay, ct).ConfigureAwait(false);
                await RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // intentional: app shutdown cancels the one-off pass; no marker is written, so it retries next launch.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BpmSplitRepair] Pass failed; it will retry on next launch.");
            }
        }, ct);
    }

    /// <summary>The edit that reconciles a grid BPM with the manual BPM: a ×2/÷2 factor, or null for a typed BPM.</summary>
    internal static double? ScaleFactorFor(double gridBpm, double manualBpm)
    {
        if (gridBpm <= 0 || manualBpm <= 0) return null;
        double ratio = manualBpm / gridBpm;
        if (Math.Abs(ratio - 2.0) / 2.0 < 0.02) return 2.0;
        if (Math.Abs(ratio - 0.5) / 0.5 < 0.02) return 0.5;
        return null;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (File.Exists(MarkerPath)) return;

        List<(string Hash, double Manual, double Grid)> split;
        using (var db = new AppDbContext())
        {
            split = (await db.LibraryEntries.AsNoTracking()
                    .Where(l => l.ManualBPM != null)
                    .Join(db.AudioFeatures.AsNoTracking()
                            // No beat grid = nothing split to repair (e.g. an orphan row with no analysis).
                            .Where(f => f.BeatGridJson != null && f.BeatGridJson.Length > 2 && f.TrackDuration > 0),
                        l => l.UniqueHash, f => f.TrackUniqueHash,
                        (l, f) => new { l.UniqueHash, Manual = l.ManualBPM!.Value, Grid = (double)f.Bpm })
                    .ToListAsync(ct).ConfigureAwait(false))
                .Where(x => x.Grid > 0 && Math.Abs(x.Manual - x.Grid) > 0.5)
                .Select(x => (x.UniqueHash, x.Manual, x.Grid))
                .ToList();
        }

        int repaired = 0, failed = 0;
        foreach (var (hash, manual, grid) in split)
        {
            ct.ThrowIfCancellationRequested();
            var result = ScaleFactorFor(grid, manual) is { } factor
                ? await _gridEdit.ScaleTempoAsync(hash, factor, ct).ConfigureAwait(false)
                : await _gridEdit.SetBpmAsync(hash, manual, ct).ConfigureAwait(false);
            if (result.Success) repaired++;
            else
            {
                failed++;
                _logger.LogWarning("[BpmSplitRepair] {Hash}: {Reason}", hash, result.Reason);
            }
        }

        string summary = $"{split.Count} split BPMs found, {repaired} repaired, {failed} failed";
        _logger.LogInformation("[BpmSplitRepair] Done: {Summary}", summary);
        WriteMarker(summary);

        if (repaired > 0 || failed > 0)
            _eventBus.Publish(new ToastRequestedEvent(
                failed == 0 ? "BPM corrections applied to the grid" : "Some BPM corrections couldn't be applied",
                failed == 0
                    ? $"Your earlier BPM fixes on {repaired} tracks now also update their beat grid, transitions and cues."
                    : $"Applied {repaired} earlier BPM fixes to the beat grid; {failed} couldn't be applied (see the log).",
                failed == 0 ? NotificationType.Success : NotificationType.Warning, TimeSpan.FromSeconds(8)));
    }

    private void WriteMarker(string summary)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, $"{DateTime.UtcNow:O}\n{summary}\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BpmSplitRepair] Could not write marker {Path}", MarkerPath);
        }
    }
}
