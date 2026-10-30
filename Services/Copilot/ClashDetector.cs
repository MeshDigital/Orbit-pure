using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.Services.Copilot;

/// <summary>A vocal presence interval within a track's own timeline (seconds).</summary>
public sealed record VocalSegment(double StartSeconds, double EndSeconds);

/// <summary>Per-track data the Clash Detector needs to sweep the spine.</summary>
public sealed record TrackClashData(
    string TrackUniqueHash,
    double MixOutSeconds,
    double MixInSeconds,
    int BarsBeforeMix,
    int CamelotNumber,
    IReadOnlyList<VocalSegment> VocalSegments);

/// <summary>
/// Slice K7c: linear timeline sweep over a set spine. Flags vocal collisions and harmonic dissonance
/// strictly within the active mix window — the <c>BarsBeforeMix</c> intersection of the outgoing
/// track's mix-out cue and the incoming track's mix-in cue. Vocals or keys clashing outside that
/// window are irrelevant.
/// </summary>
public static class ClashDetector
{
    public const string VocalOverlapReason = "VocalOverlapDuringMix";
    public const string HarmonicDissonanceReason = "HarmonicDissonance";

    /// <summary>Parses a Camelot key ("8A", "12B") to its wheel number (1-12); -1 when unknown.</summary>
    public static int CamelotNumberFromKey(string? camelotKey)
    {
        if (string.IsNullOrWhiteSpace(camelotKey))
            return -1;

        var digits = new string(camelotKey.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) && number is >= 1 and <= 12 ? number : -1;
    }

    /// <summary>Wheel distance between two Camelot numbers (1-12), wrapping around the wheel.</summary>
    public static int CamelotDistance(int a, int b)
    {
        if (a <= 0 || b <= 0)
            return int.MaxValue;
        var d = Math.Abs(a - b);
        return Math.Min(d, 12 - d);
    }

    public static IReadOnlyList<ClashReport> DetectClashes(
        IReadOnlyList<TrackClashData> spine,
        double bpm,
        int maxCamelotDistance = 1)
    {
        var reports = new List<ClashReport>();
        var barSeconds = TransitionWaveformLayout.BarSeconds(bpm);

        for (var i = 1; i < spine.Count; i++)
        {
            var outgoing = spine[i - 1];
            var incoming = spine[i];

            var window = outgoing.BarsBeforeMix * barSeconds;

            // The active mix: A's outgoing window and B's incoming window.
            var aWindowStart = outgoing.MixOutSeconds - window;
            var aWindowEnd = outgoing.MixOutSeconds;
            var bWindowStart = incoming.MixInSeconds;
            var bWindowEnd = incoming.MixInSeconds + window;

            var aVocals = HasVocalInWindow(outgoing.VocalSegments, aWindowStart, aWindowEnd);
            var bVocals = HasVocalInWindow(incoming.VocalSegments, bWindowStart, bWindowEnd);
            if (aVocals && bVocals)
                reports.Add(new ClashReport(outgoing.TrackUniqueHash, incoming.TrackUniqueHash, VocalOverlapReason));

            if (CamelotDistance(outgoing.CamelotNumber, incoming.CamelotNumber) > maxCamelotDistance)
                reports.Add(new ClashReport(outgoing.TrackUniqueHash, incoming.TrackUniqueHash, HarmonicDissonanceReason));
        }

        return reports;
    }

    private static bool HasVocalInWindow(IReadOnlyList<VocalSegment> segments, double start, double end)
        => segments.Any(s => s.EndSeconds > start && s.StartSeconds < end);
}
