using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Rekordbox;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Fits a constant tempo + phase to a beat tracker's tick positions, replacing two lossy
/// shortcuts that left ORBIT's grid uncorrelated with a DJ's hand-placed cues (measured with
/// Tests/CueBenchmark: 52% of Rekordbox cues on an ORBIT beat — chance level):
///
///   1. BPM came from an integer histogram bin (and for DnB, an integer half-time reading doubled),
///      so a real 174.00 track was stored as 173 or 178 — at 178 the grid slips a full beat every
///      ~11 bars.
///   2. Essentia's ticks are quantised to whole analysis frames (512 samples at 44.1 kHz = 11.6 ms),
///      so individual intervals only take a few discrete values (e.g. 172.27 / 178.2 BPM around DnB
///      tempo). Their median is one of those values; their least-squares slope over the whole track
///      is not.
///
/// The fit first finds the period most ticks agree on (a consensus search, robust to stretches where
/// the tracker wandered or dropped out), then refines time = a + b·index by least squares over only
/// the agreeing ticks. Pure, so it runs both in the analysis pipeline and as an in-place recompute
/// over already-analysed tracks' stored ticks (<see cref="BeatGridRecomputeService"/>).
/// </summary>
public static class BeatGridFitter
{
    public sealed record Fit(double PeriodSeconds, double PhaseSeconds, double Bpm, int TicksUsed, double ResidualRmsBeats);

    private const int MinTicks = 16;
    private const double MaxResidualRmsBeats = 0.12; // above this the track has no single steady tempo
    private const double MinInlierFraction = 0.4;    // below this, too few ticks agree on one grid

    /// <summary>
    /// Fits the ticks and returns the grid at the octave closest to <paramref name="bpmHint"/>
    /// (the already-decided, octave-corrected BPM — ticks are often at half-time for DnB). Returns
    /// null when there are too few ticks or no steady tempo, in which case callers keep what they had.
    /// </summary>
    public static Fit? FitTicks(IReadOnlyList<double> ticks, double bpmHint)
    {
        if (ticks.Count < MinTicks) return null;

        var intervals = new List<double>(ticks.Count - 1);
        for (int i = 1; i < ticks.Count; i++)
        {
            double iv = ticks[i] - ticks[i - 1];
            if (iv > 0.15 && iv < 3.0) intervals.Add(iv);
        }
        if (intervals.Count < MinTicks - 1) return null;

        intervals.Sort();
        double median = intervals[intervals.Count / 2];
        // The median is itself one of the frame-quantised values (can be ~2% off); the mean of the
        // single-beat intervals around it averages the quantisation out — a good centre for the
        // consensus search below.
        var singleBeat = intervals.Where(iv => Math.Abs(iv / median - 1.0) < 0.15).ToList();
        double period = singleBeat.Count > 0 ? singleBeat.Average() : median;

        // Consensus search: the tempo that the MOST ticks agree with, not the average of all of them.
        // A beat tracker often wanders for a stretch (a breakdown, a half-time section) and a plain
        // least-squares fit over every tick gets dragged 1-4 BPM off by it; the steady sections all
        // line up on one period, which this finds. Least squares then only refines it, over the
        // ticks that agree (measured with Tests/CueBenchmark --refit).
        var (b, a) = ConsensusPeriod(ticks, period); // b = period, a = phase (time of beat 0)

        var used = new bool[ticks.Count];
        var index = new long[ticks.Count];
        int usedCount = 0;
        double rms = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            // Global indexing against the current line: no error can accumulate from one bad gap.
            usedCount = 0;
            for (int i = 0; i < ticks.Count; i++)
            {
                double pos = (ticks[i] - a) / b;
                index[i] = (long)Math.Round(pos);
                used[i] = Math.Abs(pos - index[i]) <= 0.1;
                if (used[i]) usedCount++;
            }
            if (usedCount < MinTicks) return null;
            (a, b) = LeastSquares(ticks, index, used);
            if (b <= 0) return null;
        }

        double sumSq = 0;
        usedCount = 0;
        for (int i = 0; i < ticks.Count; i++)
        {
            double resid = (ticks[i] - (a + b * index[i])) / b;
            if (Math.Abs(resid) > 0.1) continue;
            usedCount++;
            sumSq += resid * resid;
        }
        rms = usedCount > 0 ? Math.Sqrt(sumSq / usedCount) : double.MaxValue;
        // No single steady tempo: too few ticks agree on one grid.
        if (usedCount < Math.Max(MinTicks, ticks.Count * MinInlierFraction) || rms > MaxResidualRmsBeats) return null;

        // Octave: the tick rate is the beat tracker's pulse, not necessarily the musical beat.
        double tickBpm = 60.0 / b;
        double multiplier = 1.0;
        if (bpmHint > 0)
        {
            double best = double.MaxValue;
            foreach (var m in new[] { 0.5, 1.0, 2.0, 4.0 })
            {
                double err = Math.Abs(Math.Log(tickBpm * m / bpmHint));
                if (err < best) { best = err; multiplier = m; }
            }
        }
        double beatPeriod = b / multiplier;

        // Phase: the earliest grid beat at or after time zero — extrapolated back from the first tick
        // rather than starting at it. Measured (Tests/CueBenchmark): a DJ's Rekordbox phrase markers
        // line up with 8-bar lines counted from this beat 45% of the time, versus 12% when the grid
        // starts at the beat tracker's first tick (which often comes a few beats into the track).
        // Rekordbox's own beat 1 — which PSSI phrase indices count from — sits here too.
        double phase = a - Math.Floor(a / beatPeriod) * beatPeriod;
        return new Fit(beatPeriod, phase, 60.0 / beatPeriod, usedCount, rms);
    }

    /// <summary>
    /// Replaces <see cref="AudioFeaturesEntity.Bpm"/>, <see cref="AudioFeaturesEntity.BeatGridJson"/>
    /// and <see cref="AudioFeaturesEntity.DownbeatOffsetSeconds"/> with the fitted grid. The downbeat
    /// keeps whatever bar-phase decision was already made — it is moved to the nearest fitted beat,
    /// not re-chosen. Returns false (entity untouched) when the ticks can't be fitted.
    /// </summary>
    public static bool ApplyTo(AudioFeaturesEntity features, IReadOnlyList<double> ticks, StructuralEvents? structuralEvents = null)
    {
        var fit = FitTicks(ticks, features.Bpm);
        if (fit is null) return false;

        double duration = features.TrackDuration > 0 ? features.TrackDuration : ticks[^1] + fit.PeriodSeconds;
        int count = Math.Max(1, (int)Math.Floor((duration - fit.PhaseSeconds) / fit.PeriodSeconds) + 1);
        var grid = new double[count];
        for (int k = 0; k < count; k++) grid[k] = Math.Round(fit.PhaseSeconds + k * fit.PeriodSeconds, 4);

        // Downbeat: structural events decide the bar phase when they can; otherwise keep the bar
        // phase already chosen (moved onto the nearest fitted beat).
        int anchorIndex;
        var barPhase = structuralEvents is null ? null : EstimateBarPhase(fit, structuralEvents);
        if (barPhase is int phase)
        {
            anchorIndex = Math.Min(phase, count - 1);
        }
        else
        {
            double oldAnchor = features.DownbeatOffsetSeconds > 0 ? features.DownbeatOffsetSeconds : ticks[0];
            anchorIndex = (int)Math.Clamp(Math.Round((oldAnchor - fit.PhaseSeconds) / fit.PeriodSeconds), 0, count - 1);
        }

        features.Bpm = (float)Math.Round(fit.Bpm, 2);
        features.BeatGridJson = JsonSerializer.Serialize(grid);
        features.DownbeatOffsetSeconds = grid[anchorIndex];
        return true;
    }

    /// <summary>
    /// Adopts Rekordbox's own analysed beat grid (PQTZ) when it demonstrably belongs to this audio.
    /// Rekordbox's grid is what a Rekordbox user's quantised cues snap to, and it carries real
    /// downbeats, which ORBIT otherwise has to estimate (bar phase right only ~55% of the time).
    ///
    /// Guard against a wrong match (lookup can fall back to filename only, and two copies of a
    /// track can differ in lead-in): at least half of ORBIT's own beat-tracker ticks must land within
    /// 70 ms of a Rekordbox beat. Half-time ticks still pass (they coincide with every other beat).
    /// Returns false, leaving the entity untouched, when the grid is missing, too short, or doesn't
    /// line up.
    /// </summary>
    public static bool ApplyRekordboxGrid(AudioFeaturesEntity features, IReadOnlyList<RekordboxBeat> beats, IReadOnlyList<double> orbitTicks)
    {
        if (beats.Count < 64) return false;
        var times = beats.Select(b => b.TimeSeconds).ToArray();
        if (features.TrackDuration > 0 && times[^1] > features.TrackDuration + 2) return false; // a longer file

        if (orbitTicks.Count >= MinTicks)
        {
            int agree = 0;
            foreach (var t in orbitTicks)
            {
                int i = Array.BinarySearch(times, t);
                if (i < 0) i = ~i;
                double nearest = double.MaxValue;
                if (i < times.Length) nearest = Math.Abs(times[i] - t);
                if (i > 0) nearest = Math.Min(nearest, Math.Abs(times[i - 1] - t));
                if (nearest <= 0.07) agree++;
            }
            if (agree < orbitTicks.Count * 0.5) return false;
        }

        var bpms = beats.Select(b => b.Bpm).Where(b => b > 0).OrderBy(b => b).ToList();
        if (bpms.Count == 0) return false;
        int firstDownbeat = beats.ToList().FindIndex(b => b.BeatInBar == 1);

        features.Bpm = (float)Math.Round(bpms[bpms.Count / 2], 2);
        features.BeatGridJson = JsonSerializer.Serialize(times);
        features.DownbeatOffsetSeconds = times[Math.Max(0, firstDownbeat)];
        return true;
    }

    /// <summary>Sub-bass structure events used to decide the downbeat (see <see cref="EstimateBarPhase"/>).</summary>
    public sealed record StructuralEvents(IReadOnlyList<double> BassReturns, IReadOnlyList<double> BassDropouts);

    /// <summary>
    /// Dropout timestamps land one beat after the bar line they belong to (the detector needs the
    /// bass to have been gone for a moment) — measured with Tests/CueBenchmark: unshifted they voted
    /// "+3 beats" far more than any other phase; shifted back one beat they agree with the returns.
    /// </summary>
    private const double DropoutLagBeats = 1.0;

    /// <summary>
    /// Picks the bar phase (which of 4 beats is the downbeat) that best lines up the sub-bass
    /// returns (the bass coming back in at a drop) and dropouts (going out at a breakdown) with bar
    /// lines. Arrangement changes land on the "1" of a bar, which a generic beat tracker's first tick
    /// or a kick-energy test over the first few ticks can't tell apart (measured: bar-line agreement
    /// with a DJ's Rekordbox cues 23% → 55%). Novelty/structural-stripping events were tried too and
    /// didn't help. Events are weighted by how close they sit to a beat at all, so an imprecise one
    /// can't outvote precise ones. Returns null when there are too few events to decide.
    /// </summary>
    public static int? EstimateBarPhase(Fit fit, StructuralEvents events)
    {
        var positions = events.BassReturns
            .Concat(events.BassDropouts.Select(t => t - DropoutLagBeats * fit.PeriodSeconds))
            .Where(t => t > fit.PhaseSeconds)
            .Select(t => (t - fit.PhaseSeconds) / fit.PeriodSeconds)
            .ToList();
        if (positions.Count < 3) return null;

        var scores = new double[4];
        foreach (var pos in positions)
        {
            long nearestBeat = (long)Math.Round(pos);
            double beatDistance = Math.Abs(pos - nearestBeat);
            if (beatDistance > 0.35) continue; // not on a beat at all — no bar information
            double weight = 1.0 - beatDistance / 0.35;
            scores[(int)(((nearestBeat % 4) + 4) % 4)] += weight;
        }

        int best = Array.IndexOf(scores, scores.Max());
        double total = scores.Sum();
        // Require a clear winner — ties/flat scores mean the events don't carry bar information.
        if (total <= 0 || scores[best] < total * 0.4) return null;
        return best;
    }

    /// <summary>Structural events already stored on <paramref name="features"/> (see <see cref="EstimateBarPhase"/>).</summary>
    public static StructuralEvents StructuralEventsFrom(AudioFeaturesEntity features)
    {
        var a = SLSKDONET.Engine.Analysis.AnalysisPipelineResultBuilder.Build(features);
        return new StructuralEvents(a.SubBassReturnTimestamps, a.SubBassDropoutTimestamps);
    }

    /// <summary>Parses stored <see cref="AudioFeaturesEntity.BeatGridJson"/>; empty on any failure.</summary>
    public static double[] ParseTicks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return Array.Empty<double>();
        try { return JsonSerializer.Deserialize<double[]>(json) ?? Array.Empty<double>(); }
        catch (JsonException) { return Array.Empty<double>(); }
    }

    /// <summary>
    /// Returns the period (within ±4% of <paramref name="roughPeriod"/>) where the most ticks fall
    /// close to a single phase, plus that phase. Coarse-to-fine: a scan in 2e-4 relative steps finds
    /// the right neighbourhood, then a tight-tolerance scan in 4e-5 steps (~0.01 BPM at DnB tempo)
    /// pins it — ~4x cheaper than one fine scan over the whole range (~160 ms/track, too slow for a
    /// library pass) with the same measured accuracy. A coarser first pass (4e-4, wide window) was
    /// faster still but measurably less accurate (86% → 80% of tracks within 0.1 BPM).
    /// </summary>
    private static (double Period, double Phase) ConsensusPeriod(IReadOnlyList<double> ticks, double roughPeriod)
    {
        var (coarse, _) = ScanPeriods(ticks, roughPeriod, -0.04, 0.04, 2e-4, windowBins: 8);
        return ScanPeriods(ticks, coarse, -4e-4, 4e-4, 4e-5, windowBins: 6);
    }

    /// <summary>Circular phase histogram per candidate period; score = ticks inside the best ±windowBins/100-beat window.</summary>
    private static (double Period, double Phase) ScanPeriods(
        IReadOnlyList<double> ticks, double centre, double relFrom, double relTo, double relStep, int windowBins)
    {
        const int Bins = 100;
        int Window = windowBins;
        var hist = new int[Bins];
        double bestPeriod = centre, bestPhase = ticks[0];
        int bestScore = -1;

        for (double rel = relFrom; rel <= relTo + 1e-12; rel += relStep)
        {
            double p = centre * (1 + rel);
            Array.Clear(hist);
            for (int i = 0; i < ticks.Count; i++)
            {
                double frac = ticks[i] / p;
                frac -= Math.Floor(frac);
                hist[Math.Min(Bins - 1, (int)(frac * Bins))]++;
            }

            // Best circular window of 2·Window+1 bins.
            int window = 0;
            for (int k = -Window; k <= Window; k++) window += hist[(k + Bins) % Bins];
            int best = window, bestBin = 0;
            for (int c = 1; c < Bins; c++)
            {
                window += hist[(c + Window) % Bins] - hist[(c - Window - 1 + Bins) % Bins];
                if (window > best) { best = window; bestBin = c; }
            }

            if (best > bestScore)
            {
                bestScore = best;
                bestPeriod = p;
                bestPhase = (bestBin + 0.5) / Bins * p;
            }
        }
        return (bestPeriod, bestPhase);
    }

    private static (double A, double B) LeastSquares(IReadOnlyList<double> t, long[] n, bool[]? include)
    {
        double sn = 0, st = 0, snn = 0, snt = 0;
        int count = 0;
        for (int i = 0; i < t.Count; i++)
        {
            if (include is not null && !include[i]) continue;
            sn += n[i]; st += t[i]; snn += (double)n[i] * n[i]; snt += n[i] * t[i];
            count++;
        }
        double denom = count * snn - sn * sn;
        if (count < 2 || denom <= 0) return (0, 0);
        double b = (count * snt - sn * st) / denom;
        return ((st - b * sn) / count, b);
    }
}
