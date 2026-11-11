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

        double downbeat = grid[anchorIndex];
        double? barAgreement = structuralEvents is null ? null : BarPhaseAgreement(fit.PeriodSeconds, downbeat, structuralEvents);
        float confidence = GridWriteRules.ComputeConfidence(fit, ticks.Count, barAgreement);
        return GridWriteRules.TryApplyAnalysisGrid(features, fit.Bpm, grid, downbeat, confidence);
    }

    /// <summary>
    /// Adopts Rekordbox's own analysed beat grid (PQTZ) when it demonstrably belongs to this audio.
    /// Rekordbox's grid is what a Rekordbox user's quantised cues snap to, and it carries real
    /// downbeats, which ORBIT otherwise has to estimate (bar phase right only ~55% of the time).
    ///
    /// Guard against a wrong match (lookup can fall back to filename only, and two copies of a
    /// track can differ in lead-in): at least half of ORBIT's own beat-tracker ticks must land within
    /// 70 ms of a Rekordbox beat. Half-time ticks still pass (they coincide with every other beat).
    /// Returns false, leaving the entity untouched, when the grid is missing, too short, doesn't
    /// line up, or the current grid is user-owned (kept as a suggestion instead).
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

        double bpm = bpms[bpms.Count / 2];
        double downbeat = times[Math.Max(0, firstDownbeat)];
        return GridWriteRules.TryApplyRekordboxGrid(features, bpm, times, downbeat);
    }

    /// <summary>One piecewise-constant tempo section of a variable-tempo grid (issue #226).</summary>
    public sealed record TempoSection(int StartBeatIndex, double StartSeconds, double Bpm);

    /// <summary>
    /// The BPM of the tempo section containing <paramref name="timeSeconds"/> (issue #226). For a
    /// steady track this is the fallback BPM. Consumers use this at a mix-in / mix-out point.
    /// </summary>
    public static double LocalBpmAt(IReadOnlyList<double> ticks, double timeSeconds, double fallbackBpm)
    {
        var sections = FitSections(ticks);
        if (sections.Count <= 1) return fallbackBpm;
        for (int i = 0; i < sections.Count; i++)
        {
            double end = i + 1 < sections.Count ? sections[i + 1].StartSeconds : double.MaxValue;
            if (timeSeconds < end) return sections[i].Bpm;
        }
        return sections[^1].Bpm;
    }

    /// <summary>
    /// Fits a variable-tempo beat list as piecewise-constant sections. Change-point detection on the
    /// rolling-<b>mean</b> beat interval finds where the tempo genuinely shifts (minimum
    /// <paramref name="minSectionBeats"/> beats; sections within <paramref name="changeThresholdRatio"/>
    /// merged), then each section's BPM is its average beat spacing (first to last beat) — the beat
    /// list stays the single source of truth, and the sections are derived, not stored. A steady
    /// track yields one section.
    /// <para>
    /// Why a mean and a 3% threshold: Essentia's raw ticks are quantised to 512-sample frames, so a
    /// steady 174 BPM track's intervals alternate between ~172.27 and ~178.2 BPM (see the class doc).
    /// A rolling median of those picks one of the two quantised values and a 0.3% threshold split on
    /// the alternation — 95 of 5,901 real tracks (all steady DnB) were flagged variable-tempo
    /// (2026-11-11). The mean averages the quantisation out; real tempo changes (transition edits,
    /// 108 → 128) are far above 3%.
    /// </para>
    /// </summary>
    public static IReadOnlyList<TempoSection> FitSections(
        IReadOnlyList<double> ticks,
        int minSectionBeats = 32,
        double changeThresholdRatio = 0.03)
    {
        if (ticks.Count < 2) return Array.Empty<TempoSection>();
        int n = ticks.Count;

        var interval = new double[n - 1];
        for (int i = 0; i < n - 1; i++) interval[i] = ticks[i + 1] - ticks[i];

        // Octave folding: beat trackers drop to half-time in breakdowns, miss beats (a 2× gap) or add
        // off-beat ticks (a ½× gap). None of that is a tempo change — a real one never halves or
        // doubles — so fold each interval onto the track's dominant beat period first. Without this,
        // 94 of 5,901 real tracks (half-time DnB breakdowns: 86 ↔ 175 BPM) still read as variable.
        var sortedIntervals = (double[])interval.Clone();
        Array.Sort(sortedIntervals);
        double dominant = sortedIntervals[sortedIntervals.Length / 2];
        if (dominant > 0)
        {
            for (int i = 0; i < interval.Length; i++)
            {
                double v = interval[i];
                if (v > dominant * 1.5) v /= Math.Round(v / dominant);           // 2 or 3 beats in one gap
                else if (v > 0 && v < dominant * 0.75) v *= Math.Round(dominant / v); // 2 or 3 ticks per beat
                interval[i] = v;
            }
        }

        // Two-sided window test: the mean interval of the W beats before idx vs the W beats after it.
        // Comparing neighbouring rolling means would only see 1/W of a step change per beat.
        const int W = 16;
        var prefix = new double[interval.Length + 1];
        for (int i = 0; i < interval.Length; i++) prefix[i + 1] = prefix[i] + interval[i];
        double MeanOf(int lo, int hi) => (prefix[hi] - prefix[lo]) / (hi - lo);

        var change = new double[interval.Length + 1];
        for (int idx = W; idx + W <= interval.Length; idx++)
        {
            double left = MeanOf(idx - W, idx), right = MeanOf(idx, idx + W);
            change[idx] = left > 0 ? Math.Abs(right - left) / left : 0;
        }

        var boundaries = new List<int>();
        int k = W;
        while (k + W <= interval.Length)
        {
            if (change[k] > changeThresholdRatio)
            {
                // Peak of this excursion = the step itself (the change ramps up and down around it).
                int peak = k;
                int j = k;
                while (j + W <= interval.Length && change[j] > changeThresholdRatio)
                {
                    if (change[j] > change[peak]) peak = j;
                    j++;
                }
                boundaries.Add(peak);
                // Refractory period: a section is at least minSectionBeats long anyway.
                k = Math.Max(j, peak + minSectionBeats);
            }
            else
            {
                k++;
            }
        }

        var sections = new List<TempoSection>();
        int start = 0;
        foreach (int boundary in boundaries.Concat(new[] { n }))
        {
            if (boundary - start >= minSectionBeats)
            {
                double bpm = SectionBpm(interval, start, boundary);
                if (bpm > 0)
                    sections.Add(new TempoSection(start, ticks[start], bpm));
            }
            start = boundary;
        }

        // Merge adjacent sections whose tempo is within the threshold (spurious splits).
        var result = new List<TempoSection>();
        foreach (var s in sections)
        {
            if (result.Count > 0 && Math.Abs(s.Bpm - result[^1].Bpm) / result[^1].Bpm < changeThresholdRatio)
                continue;
            result.Add(s);
        }

        if (result.Count > 1 && !SectionsAreReliable(result, interval, n))
        {
            // The beat tracker lost the beat rather than the music changing tempo: report one section
            // at the overall (folded) tempo. Callers then treat the track as steady, and the confidence
            // pass scores its grid low so it lands in "needs review" instead of "variable tempo".
            double mean = interval.Average();
            return mean > 0 ? new[] { new TempoSection(0, ticks[0], 60.0 / mean) } : Array.Empty<TempoSection>();
        }

        return result;
    }

    /// <summary>
    /// Sections only describe a genuine variable-tempo track when they are clean (2026-11-11, real
    /// library: the 93 tracks still flagged after octave folding were all steady DnB whose raw ticks
    /// wander in breakdowns — the same tracks the fitter rejected as "no steady tempo"):
    /// at most 4 sections; each at least 64 beats (16 bars) and internally steady (coefficient of
    /// variation of its folded intervals ≤ 4% — frame quantisation alone is ~1.7%); and no two adjacent
    /// sections at a metric ratio (4:3, 3:2, 2:1 within 3%), which is the tracker locking to a dotted,
    /// triplet or half-time pulse — a real tempo change essentially never lands on those ratios.
    /// </summary>
    private static bool SectionsAreReliable(IReadOnlyList<TempoSection> sections, double[] foldedIntervals, int beatCount)
    {
        if (sections.Count > 4) return false;
        for (int s = 0; s < sections.Count; s++)
        {
            int start = sections[s].StartBeatIndex;
            int end = s + 1 < sections.Count ? sections[s + 1].StartBeatIndex : beatCount;
            if (end - start < 64) return false;

            int count = Math.Min(end - 1, foldedIntervals.Length) - start;
            if (count <= 1) return false;
            double mean = 0;
            for (int i = start; i < start + count; i++) mean += foldedIntervals[i];
            mean /= count;
            double variance = 0;
            for (int i = start; i < start + count; i++) variance += (foldedIntervals[i] - mean) * (foldedIntervals[i] - mean);
            if (mean <= 0 || Math.Sqrt(variance / count) / mean > 0.04) return false;

            if (s > 0)
            {
                double ratio = Math.Max(sections[s].Bpm, sections[s - 1].Bpm) / Math.Min(sections[s].Bpm, sections[s - 1].Bpm);
                foreach (double metric in new[] { 4.0 / 3.0, 1.5, 2.0 })
                    if (Math.Abs(ratio - metric) / metric < 0.03) return false;
            }
        }
        return true;
    }

    private static double SectionBpm(double[] interval, int startBeat, int endBeat)
    {
        // The section spans beats [startBeat, endBeat); its intervals are interval[startBeat .. endBeat-2].
        // Average spacing (sum of intervals / count) = (last beat − first beat) / count: immune to the
        // frame quantisation that makes any single interval (and so a median) off by up to ~2%.
        int count = endBeat - startBeat - 1;
        if (count <= 0) return 0;
        double sum = 0;
        for (int i = startBeat; i < startBeat + count; i++) sum += interval[i];
        double mean = sum / count;
        return mean > 0 ? 60.0 / mean : 0;
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

    /// <summary>
    /// How well the stored grid's bar phase is supported by the music (issue #221): the weighted share
    /// of sub-bass returns / dropouts (arrangement changes land on the "1" of a bar) that fall on a
    /// bar line of the grid anchored at <paramref name="downbeatSeconds"/>, 0–1. Uses the same event
    /// weighting as <see cref="EstimateBarPhase"/>. Null when there are too few on-beat events to say —
    /// bar phase is ORBIT's weakest grid decision (right ~55% of the time on the cue benchmark), so
    /// this is the part of the confidence that actually separates good grids from bad ones.
    /// </summary>
    public static double? BarPhaseAgreement(double periodSeconds, double downbeatSeconds, StructuralEvents events)
    {
        if (periodSeconds <= 0) return null;
        var positions = events.BassReturns
            .Concat(events.BassDropouts.Select(t => t - DropoutLagBeats * periodSeconds))
            .Where(t => t > 0)
            .Select(t => (t - downbeatSeconds) / periodSeconds);

        double onBar = 0, total = 0;
        int counted = 0;
        foreach (var pos in positions)
        {
            long nearestBeat = (long)Math.Round(pos);
            double beatDistance = Math.Abs(pos - nearestBeat);
            if (beatDistance > 0.35) continue;
            double weight = 1.0 - beatDistance / 0.35;
            total += weight;
            counted++;
            if (((nearestBeat % 4) + 4) % 4 == 0) onBar += weight;
        }
        return counted >= 3 && total > 0 ? onBar / total : null;
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
