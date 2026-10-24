using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SLSKDONET.Models;

namespace SLSKDONET.Engine.Cueing;

/// <summary>Suggested section markers for a track that doesn't have them yet.</summary>
/// <param name="IntroEnd">Where the intro section ends, or null when nothing in the analysis says so.</param>
/// <param name="OutroStart">Where the outro section starts, or null.</param>
public readonly record struct SectionSuggestion(double? IntroEnd, string? IntroEndReason, double? OutroStart, string? OutroStartReason)
{
    public bool IsEmpty => IntroEnd == null && OutroStart == null;
}

/// <summary>
/// The two section markers next to the Intro cue and [OUT]: <b>Intro end</b> (where the intro
/// section ends and the track "arrives") and <b>Outro</b> (where the outro section starts: the
/// region a DJ mixes out over, up to the end of the track). They are ordinary cues with the
/// <see cref="CueRole.IntroEnd"/> / <see cref="CueRole.OutroStart"/> roles, stored in the CuePoints
/// table as memory cues and exported to Rekordbox as memory cues named "Intro end" / "Outro".
///
/// <see cref="Suggest"/> proposes them from existing analysis (phrase segments and drops) without
/// changing any analysis data.
/// </summary>
public static class SectionMarkers
{
    public const string IntroEndName = "Intro end";
    public const string OutroName = "Outro";

    // Cue data colours written to the export (Rekordbox palette), not UI chrome.
    public const string IntroEndColor = "#00CCFF";
    public const string OutroColor = "#0044FF";

    /// <summary>The fixed name a section marker exports under, or null for any other role.</summary>
    public static string? ExportName(CueRole role) => role switch
    {
        CueRole.IntroEnd => IntroEndName,
        CueRole.OutroStart => OutroName,
        _ => null,
    };

    public static string DefaultName(CueRole role) => ExportName(role) ?? role.ToString();

    /// <summary>The intro region: from the Intro cue (or the track start) to the Intro end marker;
    /// null when no intro end is marked.</summary>
    public static (double Start, double End)? IntroRegion(IEnumerable<OrbitCue> cues)
    {
        OrbitCue? end = null, intro = null;
        foreach (var c in cues)
        {
            if (c.IsLoop) continue;
            if (c.Role == CueRole.IntroEnd && end == null) end = c;
        }
        if (end == null) return null;
        foreach (var c in cues)
            if (!c.IsLoop && c.Role == CueRole.Intro && c.Timestamp < end.Timestamp && (intro == null || c.Timestamp < intro.Timestamp)) intro = c;
        return (Math.Max(0, intro?.Timestamp ?? 0), end.Timestamp);
    }

    /// <summary>The outro region: from the Outro marker to the end of the track; null when no outro
    /// is marked.</summary>
    public static (double Start, double End)? OutroRegion(IEnumerable<OrbitCue> cues, double duration)
    {
        foreach (var c in cues)
            if (!c.IsLoop && c.Role == CueRole.OutroStart)
                return (c.Timestamp, Math.Max(c.Timestamp, duration));
        return null;
    }
    public static string DefaultColor(CueRole role) => role == CueRole.IntroEnd ? IntroEndColor : OutroColor;

    /// <summary>
    /// Suggests both markers from the phrase segments and the drops:
    /// <list type="bullet">
    /// <item><b>Intro end</b>: the end of the leading "Intro" phrases; otherwise the first drop.</item>
    /// <item><b>Outro</b>: the start of the trailing "Outro" phrases (after the last drop); otherwise
    /// the phrase boundary after the last drop's main section (the run of drop phrases it starts);
    /// otherwise 32 bars after the last drop (the [OUT] rule).</item>
    /// </list>
    /// Everything lands on the bar grid. Without phrases or drops there is no suggestion.
    /// </summary>
    public static SectionSuggestion Suggest(IReadOnlyList<PhraseSegment>? phrases, IEnumerable<OrbitCue> cues,
        double bpm, double downbeat, double duration)
    {
        var segments = (phrases ?? Array.Empty<PhraseSegment>())
            .Where(p => p.Duration > 0 && p.Start >= 0 && (duration <= 0 || p.Start < duration))
            .OrderBy(p => p.Start)
            .ToList();
        var drops = cues.Where(c => c.Role == CueRole.Drop && !c.IsLoop).Select(c => c.Timestamp).OrderBy(t => t).ToList();
        double bar = bpm > 0 ? 240.0 / bpm : 0;
        double minSection = bar > 0 ? 4 * bar : 8.0;

        // ── Intro end ──
        double? introEnd = null;
        string? introReason = null;
        var leadingIntro = segments.TakeWhile(p => IsLabel(p, "intro")).ToList();
        if (leadingIntro.Count > 0)
        {
            double end = leadingIntro[^1].Start + leadingIntro[^1].Duration;
            if (end >= minSection) { introEnd = end; introReason = "end of the intro phrase"; }
        }
        if (introEnd == null && drops.Count > 0 && drops[0] >= minSection)
        {
            introEnd = drops[0];
            introReason = "first drop";
        }
        if (introEnd == null)
        {
            var firstDropPhrase = segments.FirstOrDefault(p => IsLabel(p, "drop"));
            if (firstDropPhrase != null && firstDropPhrase.Start >= minSection)
            {
                introEnd = firstDropPhrase.Start;
                introReason = "first drop phrase";
            }
        }

        // ── Outro start ──
        double? lastDrop = drops.Count > 0 ? drops[^1]
            : segments.LastOrDefault(p => IsLabel(p, "drop")) is { } dp ? dp.Start : null;
        double floor = lastDrop ?? introEnd ?? 0;
        double? outroStart = null;
        string? outroReason = null;

        var trailingOutro = Enumerable.Reverse(segments).TakeWhile(p => IsLabel(p, "outro")).ToList();
        if (trailingOutro.Count > 0 && trailingOutro[^1].Start > floor)
        {
            outroStart = trailingOutro[^1].Start;
            outroReason = "start of the outro phrase";
        }
        if (outroStart == null && lastDrop is { } d && segments.Count > 0)
        {
            double tolerance = bar > 0 ? bar : 1.0;
            int i = segments.FindIndex(p => d >= p.Start - tolerance && d < p.Start + p.Duration);
            if (i >= 0)
            {
                // The main section: the phrase the drop starts and the drop phrases straight after it.
                int j = i;
                while (j + 1 < segments.Count && IsLabel(segments[j + 1], "drop")) j++;
                double end = segments[j].Start + segments[j].Duration;
                bool roomToMix = duration <= 0 || end <= duration - 2 * minSection;
                if (end >= d + minSection && roomToMix)
                {
                    outroStart = end;
                    outroReason = "phrase boundary after the last drop's main section";
                }
            }
        }
        if (outroStart == null && drops.Count > 0 &&
            DropCountdownCues.OutTime(drops[^1], bpm, downbeat, duration) is { } outAt)
        {
            outroStart = outAt;
            outroReason = $"{DropCountdownCues.OutBarsAfterLastDrop} bars after the last drop";
        }

        introEnd = introEnd is { } ie ? Snap(ie, bar, downbeat, duration) : null;
        outroStart = outroStart is { } os ? Snap(os, bar, downbeat, duration) : null;
        if (introEnd is { } a && outroStart is { } b && b <= a + (bar > 0 ? bar : 1)) { outroStart = null; outroReason = null; }
        return new SectionSuggestion(introEnd, introReason, outroStart, outroReason);
    }

    /// <summary>Nearest bar line on the track's grid (unchanged when the tempo is unknown).</summary>
    public static double Snap(double seconds, double bar, double downbeat, double duration)
    {
        double t = bar > 0 ? downbeat + Math.Round((seconds - downbeat) / bar) * bar : seconds;
        return Math.Clamp(t, 0, duration > 0 ? duration : double.MaxValue);
    }

    /// <summary>"1:02 (bar 33)" for a status line or hint (invariant formatting).</summary>
    public static string Describe(double seconds, double bpm, double downbeat)
    {
        string time = CueForgeGridFormat(seconds);
        if (bpm <= 0) return time;
        long barNo = (long)Math.Floor((seconds - downbeat) / (240.0 / bpm) + 1e-6) + 1;
        return $"{time} (bar {barNo.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string CueForgeGridFormat(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    private static bool IsLabel(PhraseSegment p, string label) =>
        p.Label?.Contains(label, StringComparison.OrdinalIgnoreCase) == true;
}
