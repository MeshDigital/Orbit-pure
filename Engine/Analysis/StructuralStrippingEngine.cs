using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Detects House/Techno-family "structural stripping" breakdowns: kick and bass genuinely
/// absent (not just quiet) for a sustained stretch, then a sharp, confirmed return — the
/// four-on-the-floor analogue of <see cref="SubBassDropoutEngine"/>'s DnB-oriented sub-bass
/// dropout detection, but checking a fundamentally different signal shape.
///
/// Why a separate engine instead of reusing <see cref="SubBassDropoutEngine"/> directly:
///   That engine only measures sustained energy *level* in the sub-bass band. A House/Techno
///   breakdown genuinely removes the kick and bass, but many tracks still carry a faint,
///   periodic low-end presence (a sidechained pad, a quiet sub note) through the "breakdown" —
///   a pure energy-level check reads that as "not dropped out" or misfires on a merely-quiet
///   section that still has its kick. What actually signals a real structural-stripping
///   breakdown is the *absence of periodic low-band transients* (no kick hits), not just low
///   average energy. This engine adds that periodicity check on top of the same sustained-low
///   / confirmed-return shape <see cref="SubBassDropoutEngine"/> already uses.
///
/// Reuses <see cref="SubBassDropoutEngine.ComputeBandEnergyCurve"/> for the underlying
/// Butterworth-filtered windowed-RMS curve (at a wider ~250 Hz cutoff — wide enough to catch
/// kick fundamental + punch, not just sub-bass) rather than duplicating the filter/windowing
/// math.
/// </summary>
public sealed class StructuralStrippingEngine
{
    private const double MidLowCutoffHz = 250.0;
    private const double DropoutThresholdRatio = 0.25; // below 25% of track-average = candidate dropout
    private const double ReturnThresholdRatio = 0.60;  // above 60% of track-average = candidate return
    private const double MinDropoutSeconds = 2.0;

    // A window counts as containing a kick onset when its novelty (first-difference from the
    // previous window) exceeds this fraction of the track's average novelty. At the ~0.5s window
    // resolution shared with SubBassDropoutEngine, one beat lands in roughly one window across the
    // whole House/Techno BPM range (118-150), so a per-window onset-presence check is a workable
    // proxy for beat-periodicity without needing a separate finer-grained onset sub-pipeline.
    private const double OnsetNoveltyRatio = 1.2;
    private const double MinOnsetCoverageForRealKick = 0.20; // >=20% of windows show an onset => kick still present, not a real strip

    private readonly SubBassDropoutEngine _bandEngine;

    public StructuralStrippingEngine(SubBassDropoutEngine? bandEngine = null)
    {
        _bandEngine = bandEngine ?? new SubBassDropoutEngine();
    }

    /// <summary>
    /// Detects structural-stripping start/return timestamps from a raw mono PCM signal.
    /// Returns empty lists (never throws) on degenerate input.
    /// </summary>
    public (List<double> StartTimestamps, List<double> ReturnTimestamps) DetectStructuralStripping(
        float[] monoSignal, int sampleRate)
    {
        var starts = new List<double>();
        var returns = new List<double>();

        if (monoSignal == null || monoSignal.Length == 0 || sampleRate <= 0)
            return (starts, returns);

        var bandCurve = _bandEngine.ComputeBandEnergyCurve(monoSignal, sampleRate, MidLowCutoffHz);
        if (bandCurve.Length < 4)
            return (starts, returns);

        double windowSeconds = _bandEngine.WindowSeconds;
        var novelty = ComputeNovelty(bandCurve);
        double noveltyMean = novelty.Length > 0 ? novelty.Average() : 0.0;
        double onsetThreshold = noveltyMean * OnsetNoveltyRatio;

        float trackMean = bandCurve.Average();
        if (trackMean < 1e-8f) return (starts, returns);

        float dropoutThreshold = trackMean * (float)DropoutThresholdRatio;
        float returnThreshold = trackMean * (float)ReturnThresholdRatio;
        int minDropoutWindows = (int)Math.Ceiling(MinDropoutSeconds / windowSeconds);

        bool inCandidateDropout = false;
        int candidateStartWindow = -1;
        int consecutiveLow = 0;

        for (int i = 0; i < bandCurve.Length; i++)
        {
            double ts = i * windowSeconds;

            if (!inCandidateDropout)
            {
                if (bandCurve[i] < dropoutThreshold)
                {
                    consecutiveLow++;
                    if (consecutiveLow >= minDropoutWindows && candidateStartWindow < 0)
                        candidateStartWindow = i - consecutiveLow + 1;
                }
                else
                {
                    consecutiveLow = 0;
                    candidateStartWindow = -1;
                }

                if (candidateStartWindow >= 0 && consecutiveLow >= minDropoutWindows)
                {
                    inCandidateDropout = true;
                }
            }
            else
            {
                if (bandCurve[i] >= returnThreshold)
                {
                    // Confirm both ends of the candidate window before accepting it as a real
                    // structural-stripping event: the dropout stretch must show near-zero kick
                    // onset coverage (periodicity absent, not just quiet), and the return itself
                    // must show a real onset-density spike (confirms kick re-entry, not just a
                    // gradual swell back up).
                    int dropoutEndWindow = i - 1;
                    if (dropoutEndWindow > candidateStartWindow)
                    {
                        double onsetCoverage = OnsetCoverage(novelty, candidateStartWindow, dropoutEndWindow, onsetThreshold);
                        bool returnConfirmed = IsReturnConfirmed(novelty, i, novelty.Length, onsetThreshold);

                        if (onsetCoverage < MinOnsetCoverageForRealKick && returnConfirmed)
                        {
                            starts.Add(candidateStartWindow * windowSeconds);
                            returns.Add(ts);
                        }
                    }

                    inCandidateDropout = false;
                    consecutiveLow = 0;
                    candidateStartWindow = -1;
                }
            }
        }

        return (starts, returns);
    }

    private static float[] ComputeNovelty(float[] curve)
    {
        var novelty = new float[curve.Length];
        for (int i = 1; i < curve.Length; i++)
            novelty[i] = Math.Max(0f, curve[i] - curve[i - 1]);
        return novelty;
    }

    private static double OnsetCoverage(float[] novelty, int startWindow, int endWindow, double onsetThreshold)
    {
        int span = endWindow - startWindow + 1;
        if (span <= 0) return 0.0;

        int onsetWindows = 0;
        for (int i = startWindow; i <= endWindow && i < novelty.Length; i++)
        {
            if (novelty[i] > onsetThreshold) onsetWindows++;
        }
        return onsetWindows / (double)span;
    }

    private static bool IsReturnConfirmed(float[] novelty, int returnWindow, int length, double onsetThreshold)
    {
        // Look at the return window and the following window for a confirmed onset spike —
        // a gradual energy swell without a sharp per-window novelty peak isn't a real kick re-entry.
        for (int i = returnWindow; i < Math.Min(length, returnWindow + 2); i++)
        {
            if (i >= 0 && i < novelty.Length && novelty[i] > onsetThreshold) return true;
        }
        return false;
    }
}
