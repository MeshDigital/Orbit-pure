using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// One-time pass that gives every already-analysed grid a <see cref="Data.Entities.AudioFeaturesEntity.GridConfidence"/>
/// and an up-to-date <see cref="Data.Entities.AudioFeaturesEntity.IsVariableTempo"/> flag (issues #221, #226),
/// purely from stored data — no decode, no grid change.
/// <para>
/// Why a separate pass: confidence and the variable-tempo flag were only set when a grid was (re)fitted,
/// and the re-fit pass (<see cref="BeatGridRecomputeService"/>, marker <c>beatgrid-fit-v1</c>) had already
/// run on existing installs, so it never ran again — 5,704 of 5,901 tracks of a real library had no
/// confidence and the "Grid review" filter could never match (2026-11-11).
/// </para>
/// Only <c>GridConfidence</c> and <c>IsVariableTempo</c> are written; Bpm, the beat list, Bar 1 and
/// <c>GridSource</c> are never touched, and user-owned grids are skipped (they are 1.0 by definition).
/// </summary>
public sealed class GridConfidenceBackfillService
{
    private const string PassName = "grid-confidence-v1";
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(150);

    private readonly ILogger<GridConfidenceBackfillService> _logger;

    public GridConfidenceBackfillService(ILogger<GridConfidenceBackfillService> logger)
    {
        _logger = logger;
    }

    /// <summary>Where the "done" marker lives. Overridable in tests.</summary>
    internal string DataDirectory { get; set; } = Path.Combine(OrbitPaths.RoamingRoot, "ORBIT", "Backups");

    private string MarkerPath => Path.Combine(DataDirectory, $"{PassName}.done");

    /// <summary>Starts the pass in the background if it hasn't run yet. Never throws.</summary>
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
                _logger.LogError(ex, "[GridConfidenceBackfill] Pass failed; it will retry on next launch.");
            }
        }, ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (File.Exists(MarkerPath)) return;

        List<string> hashes;
        using (var db = new AppDbContext())
        {
            hashes = await db.AudioFeatures.AsNoTracking()
                .Where(f => f.Bpm > 0 && f.BeatGridJson != null && f.BeatGridJson.Length > 2
                            && f.GridSource != GridWriteRules.SourceUser && !f.GridVerified)
                .Select(f => f.TrackUniqueHash)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        int scored = 0, review = 0, variable = 0, unflagged = 0;
        var started = DateTime.UtcNow;
        for (int i = 0; i < hashes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var context = new AppDbContext();
            var f = await context.AudioFeatures.FirstOrDefaultAsync(x => x.TrackUniqueHash == hashes[i], ct).ConfigureAwait(false);
            if (f is null || GridWriteRules.IsUserOwned(f)) continue;

            var (confidence, isVariable) = Score(f);
            bool changed = false;
            if (f.GridConfidence is null || Math.Abs(f.GridConfidence.Value - confidence) > 1e-4)
            {
                f.GridConfidence = confidence;
                changed = true;
            }
            if (f.IsVariableTempo != isVariable)
            {
                if (!isVariable) unflagged++;
                f.IsVariableTempo = isVariable;
                changed = true;
            }
            if (changed) await context.SaveChangesAsync(ct).ConfigureAwait(false);

            scored++;
            if (confidence < GridWriteRules.ReviewThreshold) review++;
            if (isVariable) variable++;

            if ((i + 1) % 250 == 0)
                await Task.Delay(50, ct).ConfigureAwait(false); // let foreground writers in
        }

        string summary = $"{scored} grids scored, {review} below {GridWriteRules.ReviewThreshold.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} (needs review), " +
                         $"{variable} variable-tempo, {unflagged} variable-tempo flags cleared, {(DateTime.UtcNow - started).TotalSeconds:F0}s";
        _logger.LogInformation("[GridConfidenceBackfill] Done: {Summary}", summary);
        WriteMarker(summary);
    }

    /// <summary>
    /// Confidence + variable-tempo verdict for a stored grid (pure; see <see cref="GridWriteRules.ComputeConfidence"/>).
    /// Rekordbox grids are 1.0 (real downbeats). A fitted constant grid has a perfect tempo part, so only
    /// its bar phase is scored (no fit needed); raw beat-tracker ticks are fitted first.
    /// </summary>
    internal static (float Confidence, bool IsVariable) Score(Data.Entities.AudioFeaturesEntity f)
    {
        var ticks = BeatGridFitter.ParseTicks(f.BeatGridJson);
        bool isVariable = ticks.Length >= 64 && BeatGridFitter.FitSections(ticks).Count > 1;

        if (string.Equals(f.GridSource, GridWriteRules.SourceRekordbox, StringComparison.Ordinal))
            return (1.0f, isVariable);
        if (ticks.Length < 2) return (0.0f, false);

        var events = BeatGridFitter.StructuralEventsFrom(f);
        double period = f.Bpm > 0 ? 60.0 / f.Bpm : 0;

        if (IsRegularGrid(ticks))
            return (GridWriteRules.Combine(1.0, BeatGridFitter.BarPhaseAgreement(period, f.DownbeatOffsetSeconds, events)), isVariable);

        var fit = BeatGridFitter.FitTicks(ticks, f.Bpm);
        if (fit is null)
        {
            // No single steady tempo. A genuine tempo-change track is a grid worth a look (mid score);
            // an unsteady one the fitter couldn't explain is low.
            return (isVariable ? 0.5f : 0.3f, isVariable);
        }
        return (GridWriteRules.ComputeConfidence(fit, ticks.Length,
            BeatGridFitter.BarPhaseAgreement(fit.PeriodSeconds, f.DownbeatOffsetSeconds, events)), isVariable);
    }

    /// <summary>True for a fitted / generated constant grid (every interval within 1 ms of the median).</summary>
    internal static bool IsRegularGrid(double[] ticks)
    {
        if (ticks.Length < 3) return false;
        var intervals = new double[ticks.Length - 1];
        for (int i = 1; i < ticks.Length; i++) intervals[i - 1] = ticks[i] - ticks[i - 1];
        var sorted = (double[])intervals.Clone();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];
        return intervals.All(iv => Math.Abs(iv - median) <= 0.001);
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
            _logger.LogWarning(ex, "[GridConfidenceBackfill] Could not write marker {Path}", MarkerPath);
        }
    }
}
