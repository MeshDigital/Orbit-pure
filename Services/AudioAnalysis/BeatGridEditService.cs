using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>The outcome of a beat-grid edit. <see cref="Success"/> is false when nothing was written.</summary>
public sealed record GridEditResult(bool Success, string? Reason = null)
{
    public static GridEditResult Ok() => new(true);
    public static GridEditResult Failed(string reason) => new(false, reason);
}

/// <summary>
/// The single write path for beat-grid edits (set BPM, ×2/÷2, slip, set Bar 1, reset). Every grid
/// change is keyed on the track hash and, unlike the old <c>TrackRepository.UpdateBpmAsync</c> path,
/// updates <see cref="AudioFeaturesEntity.Bpm"/>, <see cref="AudioFeaturesEntity.BeatGridJson"/> and
/// <see cref="AudioFeaturesEntity.DownbeatOffsetSeconds"/> together with the library BPM/ManualBPM, so
/// Cue Forge, transition planning, cue generation and the Rekordbox export all read the same tempo.
/// The beat list is always changed with the number — it must never disagree with <c>Bpm</c>.
/// </summary>
public interface IBeatGridEditService
{
    /// <summary>Typed/tap BPM: regenerates a constant grid from Bar 1 at the new tempo.</summary>
    Task<GridEditResult> SetBpmAsync(string hash, double bpm, CancellationToken ct);

    /// <summary>×2 (factor 2.0) or ÷2 (factor 0.5). ×2 inserts midpoints; ÷2 keeps every other beat, keeping the Bar 1 beat.</summary>
    Task<GridEditResult> ScaleTempoAsync(string hash, double factor, CancellationToken ct);

    /// <summary>Shifts every beat (and Bar 1) by <paramref name="deltaSeconds"/>; tempo is unchanged.</summary>
    Task<GridEditResult> SlipAsync(string hash, double deltaSeconds, CancellationToken ct);

    /// <summary>Moves Bar 1 to the nearest beat; tempo and the beat list are unchanged.</summary>
    Task<GridEditResult> SetDownbeatAsync(string hash, double barOneSeconds, CancellationToken ct);

    /// <summary>Restores the analysed BPM and downbeat, regenerating the constant grid.</summary>
    Task<GridEditResult> ResetToAnalysedAsync(string hash, double analysedBpm, double analysedDownbeat, CancellationToken ct);
}

public sealed class BeatGridEditService : IBeatGridEditService
{
    private const double MinBpm = 40;
    private const double MaxBpm = 400;

    private readonly DatabaseService _database;
    private readonly IEventBus? _eventBus;
    private readonly ILogger<BeatGridEditService> _logger;

    public BeatGridEditService(DatabaseService database, IEventBus? eventBus, ILogger<BeatGridEditService> logger)
    {
        _database = database;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<GridEditResult> SetBpmAsync(string hash, double bpm, CancellationToken ct)
    {
        if (bpm < MinBpm || bpm > MaxBpm)
            return GridEditResult.Failed($"BPM {bpm:0.#} is outside the supported range ({MinBpm:0}–{MaxBpm:0}).");

        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");

        double downbeat = features.DownbeatOffsetSeconds;
        double[] beats = ConstantGrid(downbeat, bpm, features.TrackDuration);
        return await WriteGridAsync(hash, features, bpm, beats, downbeat, ct).ConfigureAwait(false);
    }

    public async Task<GridEditResult> ScaleTempoAsync(string hash, double factor, CancellationToken ct)
    {
        if (factor != 2.0 && factor != 0.5)
            return GridEditResult.Failed("Only ×2 and ÷2 tempo scaling is supported.");

        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");

        double[] oldBeats = BeatGridFitter.ParseTicks(features.BeatGridJson);
        if (oldBeats.Length < 2)
            return GridEditResult.Failed("This track has no beat grid to rescale.");

        double oldBpm = features.Bpm > 0 ? features.Bpm : MedianIntervalBpm(oldBeats);
        double newBpm = oldBpm * factor;
        if (newBpm < MinBpm || newBpm > MaxBpm)
            return GridEditResult.Failed($"The result ({newBpm:0.#} BPM) is outside the supported range ({MinBpm:0}–{MaxBpm:0}).");

        double period = MedianInterval(oldBeats);
        double downbeat = features.DownbeatOffsetSeconds;
        double[] newBeats = factor > 1.0
            ? InsertMidpoints(oldBeats, period)
            : KeepEveryOtherBeat(oldBeats, downbeat);

        return await WriteGridAsync(hash, features, newBpm, newBeats, downbeat, ct).ConfigureAwait(false);
    }

    public async Task<GridEditResult> SlipAsync(string hash, double deltaSeconds, CancellationToken ct)
    {
        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");

        double[] beats = BeatGridFitter.ParseTicks(features.BeatGridJson);
        double bpm = features.Bpm > 0 ? features.Bpm : (beats.Length >= 2 ? MedianIntervalBpm(beats) : 0);

        double downbeat = features.DownbeatOffsetSeconds + deltaSeconds;
        double shift = deltaSeconds;
        if (downbeat < 0 && bpm > 0)
        {
            // Wrap by one bar: the grid is periodic at the bar, so this preserves phase while keeping
            // every beat non-negative and Bar 1 a beat.
            double bar = 240.0 / bpm;
            downbeat += bar;
            shift += bar;
        }

        double[] newBeats = beats.Select(b => Math.Round(b + shift, 4)).ToArray();
        return await WriteGridAsync(hash, features, bpm, newBeats, downbeat, ct).ConfigureAwait(false);
    }

    public async Task<GridEditResult> SetDownbeatAsync(string hash, double barOneSeconds, CancellationToken ct)
    {
        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");

        double[] beats = BeatGridFitter.ParseTicks(features.BeatGridJson);
        double bpm = features.Bpm > 0 ? features.Bpm : (beats.Length >= 2 ? MedianIntervalBpm(beats) : 0);
        double snapped = SnapToBeat(beats, barOneSeconds);

        return await WriteGridAsync(hash, features, bpm, beats, snapped, ct).ConfigureAwait(false);
    }

    public async Task<GridEditResult> ResetToAnalysedAsync(string hash, double analysedBpm, double analysedDownbeat, CancellationToken ct)
    {
        if (analysedBpm < MinBpm || analysedBpm > MaxBpm)
            return GridEditResult.Failed($"The analysed BPM {analysedBpm:0.#} is outside the supported range.");

        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");

        double[] beats = ConstantGrid(analysedDownbeat, analysedBpm, features.TrackDuration);
        return await WriteGridAsync(hash, features, analysedBpm, beats, analysedDownbeat, ct).ConfigureAwait(false);
    }

    private async Task<GridEditResult> WriteGridAsync(
        string hash, AudioFeaturesEntity features, double bpm, double[] beats, double downbeat, CancellationToken ct)
    {
        features.Bpm = (float)Math.Round(bpm, 2);
        features.BeatGridJson = JsonSerializer.Serialize(beats);
        features.DownbeatOffsetSeconds = Math.Round(downbeat, 4);

        try
        {
            bool wrote = await _database.ApplyGridEditAsync(features, ct).ConfigureAwait(false);
            if (!wrote)
                return GridEditResult.Failed("Couldn't save the beat grid: the track has no analysis row.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BeatGridEdit: failed to persist grid for {Hash}", hash);
            return GridEditResult.Failed($"Couldn't save the beat grid change: {ex.Message}");
        }

        try
        {
            _eventBus?.Publish(new TrackAnalysisUpdatedEvent(hash, DateTime.UtcNow));
            _eventBus?.Publish(new TrackMetadataUpdatedEvent(hash));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BeatGridEdit: publishing the update event failed for {Hash}", hash);
        }

        return GridEditResult.Ok();
    }

    /// <summary>
    /// A constant beat grid at <paramref name="bpm"/> whose Bar 1 (<paramref name="downbeat"/>) is a
    /// beat, covering the whole track. The earliest beat sits at or after t = 0 and pickups before
    /// Bar 1 are included (negative beat indices relative to Bar 1).
    /// </summary>
    internal static double[] ConstantGrid(double downbeat, double bpm, double duration)
    {
        if (bpm <= 0 || duration <= 0)
            return new[] { Math.Round(Math.Max(0, downbeat), 4) };

        double period = 60.0 / bpm;
        int n0 = (int)-Math.Floor(downbeat / period);
        int n1 = (int)Math.Ceiling((duration - downbeat) / period);
        if (n1 < n0) return new[] { Math.Round(downbeat, 4) };

        var beats = new double[n1 - n0 + 1];
        for (int n = n0; n <= n1; n++) beats[n - n0] = Math.Round(downbeat + n * period, 4);
        return beats;
    }

    /// <summary>×2: every beat plus its midpoint.</summary>
    internal static double[] InsertMidpoints(double[] beats, double period)
    {
        var result = new List<double>(beats.Length * 2);
        foreach (var b in beats)
        {
            result.Add(b);
            result.Add(Math.Round(b + period / 2.0, 4));
        }
        result.Sort();
        return result.Distinct().ToArray();
    }

    /// <summary>÷2: every other beat, keeping the beat that sits on Bar 1 (<paramref name="downbeat"/>).</summary>
    internal static double[] KeepEveryOtherBeat(double[] beats, double downbeat)
    {
        int idx = NearestBeatIndex(beats, downbeat);
        if (idx < 0) return beats;

        var result = new List<double>();
        for (int i = idx; i < beats.Length; i += 2) result.Add(beats[i]);
        for (int i = idx - 2; i >= 0; i -= 2) result.Add(beats[i]);
        result.Sort();
        return result.ToArray();
    }

    internal static double MedianInterval(double[] beats)
    {
        if (beats.Length < 2) return 0;
        var intervals = new double[beats.Length - 1];
        for (int i = 1; i < beats.Length; i++) intervals[i - 1] = beats[i] - beats[i - 1];
        Array.Sort(intervals);
        return intervals[intervals.Length / 2];
    }

    private static double MedianIntervalBpm(double[] beats) =>
        beats.Length >= 2 && MedianInterval(beats) > 0 ? 60.0 / MedianInterval(beats) : 0;

    private static int NearestBeatIndex(double[] beats, double downbeat)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < beats.Length; i++)
        {
            double d = Math.Abs(beats[i] - downbeat);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    private static double SnapToBeat(double[] beats, double seconds)
    {
        int idx = NearestBeatIndex(beats, seconds);
        return idx < 0 ? Math.Round(seconds, 4) : beats[idx];
    }
}
