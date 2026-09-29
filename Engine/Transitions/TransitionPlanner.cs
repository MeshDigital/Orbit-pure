using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Transitions;

/// <summary>What the planner needs to know about one track. Times in seconds.</summary>
public sealed record TrackStructure
{
    public double DurationSeconds { get; init; }
    public double Bpm { get; init; }
    public double FirstDownbeat { get; init; }
    /// <summary>First "Intro"-type cue (usually the first downbeat).</summary>
    public double? IntroCue { get; init; }
    public double? Drop1 { get; init; }
    public double? Drop2 { get; init; }
    /// <summary>Last "Outro"-type cue.</summary>
    public double? OutroCue { get; init; }
    /// <summary>Last moment the track is audible (Mixxx's "last sound", -60 dB-ish); the duration when unknown.</summary>
    public double? LastSound { get; init; }
    public double? VocalStart { get; init; }
    public double? VocalEnd { get; init; }
    /// <summary>0..1.</summary>
    public double Energy { get; init; } = 0.5;
}

/// <summary>Mixxx-style regions, derived from a <see cref="TrackStructure"/>.</summary>
public sealed record TrackSections(double IntroStart, double IntroEnd, double OutroStart, double OutroEnd, double BarSeconds)
{
    public double IntroBars => (IntroEnd - IntroStart) / BarSeconds;
    public double OutroBars => (OutroEnd - OutroStart) / BarSeconds;
}

public enum PlannedTransitionKind
{
    /// <summary>Outgoing outro into incoming intro, long and smooth (Vande Veire's "relaxed").</summary>
    Relaxed,
    /// <summary>Incoming drop lands as the outgoing main section ends; hard bass swap on the drop.</summary>
    Rolling,
    /// <summary>Incoming Drop 1 lands on outgoing Drop 2 — the DJ "double drop"; hard bass swap on the drop.</summary>
    DropSync,
}

/// <summary>A complete, executable transition: where to leave, where to enter, how long, which effect.</summary>
public sealed record TransitionPlan(
    PlannedTransitionKind Kind,
    double SourceTriggerSeconds,
    double TargetTriggerSeconds,
    int DurationBars,
    double DurationSeconds,
    bool VocalClash,
    string Reason)
{
    /// <summary>Name shown in the UI and passed to TransitionPresetLibrary.</summary>
    public string PresetName => Kind switch
    {
        PlannedTransitionKind.Rolling => "Rolling",
        PlannedTransitionKind.DropSync => "Drop Sync",
        _ => "Relaxed",
    };
}

/// <summary>
/// Plans a mix between two tracks from their structure, combining three ideas:
/// <list type="bullet">
/// <item><b>Mixxx Auto DJ</b>: intros and outros are regions, not points, and a transition must
/// fit inside them — its length is the shorter of the outgoing outro and the incoming intro.
/// This replaces mixing out at a single "Outro" cue, which auto-analysis places 0–10 s before
/// the end (verified 2026-09-29), leaving no room for an 8–16-bar transition.</item>
/// <item><b>Phrase quantisation</b>: lengths are whole 8/16/32-bar phrases, and every trigger sits
/// on the bar grid — DnB (and most dance music) moves in 16/32-bar phrases.</item>
/// <item><b>Vande Veire's DnB auto-DJ</b>: three transition types — relaxed (outro→intro),
/// rolling (incoming drop right as the outgoing main section ends) and double drop (both drops
/// together) — and avoiding vocals from both tracks in the same window.</item>
/// </list>
/// Pure and deterministic; <see cref="Services.Transitions.TransitionPlanService"/> feeds it from the DB.
/// </summary>
public static class TransitionPlanner
{
    private static readonly int[] PhraseLengths = { 32, 16, 8 };

    /// <summary>Beats per bar — the whole library is 4/4 dance music.</summary>
    private const int BeatsPerBar = 4;

    public static TrackSections ComputeSections(TrackStructure t)
    {
        double bar = BarSeconds(t.Bpm);
        double duration = Math.Max(t.DurationSeconds, bar);
        double outroEnd = Math.Clamp(t.LastSound ?? duration, bar, duration);

        double introStart = Math.Clamp(t.IntroCue ?? t.FirstDownbeat, 0, outroEnd);
        // The intro runs until the first drop (the main part starts there), at least 8 bars.
        double introEnd = t.Drop1 is { } d1 && d1 > introStart + 4 * bar
            ? d1
            : introStart + 32 * bar;
        introEnd = Math.Min(introEnd, outroEnd);

        // The outro starts where the last main section ends. A DnB main section after the last
        // drop runs ~32 bars; the analysed Outro cue is only trusted when it leaves real room.
        double lastDrop = t.Drop2 ?? t.Drop1 ?? introEnd;
        double mainEnd = SnapToGrid(lastDrop + 32 * bar, t.FirstDownbeat, bar);
        double outroStart = t.OutroCue is { } oc && outroEnd - oc >= 16 * bar ? oc : mainEnd;
        // Always leave at least 16 bars for a transition, but never before the last drop.
        if (outroEnd - outroStart < 16 * bar)
            outroStart = SnapToGrid(outroEnd - 16 * bar, t.FirstDownbeat, bar);
        outroStart = Math.Max(outroStart, Math.Min(lastDrop, outroEnd - 8 * bar));
        outroStart = Math.Clamp(outroStart, 0, outroEnd - bar);

        return new TrackSections(introStart, introEnd, outroStart, outroEnd, bar);
    }

    /// <summary>
    /// Plans the transition out of <paramref name="outgoing"/> into <paramref name="incoming"/>.
    /// <paramref name="compatibility"/> is the pair's 0–100 key/energy score (TrackPairCompatibilityScorer).
    /// </summary>
    public static TransitionPlan Plan(TrackStructure outgoing, TrackStructure incoming, double compatibility)
    {
        var o = ComputeSections(outgoing);
        var i = ComputeSections(incoming);
        bool tempoLocked = TempoLocked(outgoing, incoming);
        bool bothEnergetic = outgoing.Energy >= 0.55 && incoming.Energy >= 0.55;

        var candidates = new List<TransitionPlan>();
        if (tempoLocked && bothEnergetic && compatibility >= 75)
            candidates.AddIfNotNull(DropSync(outgoing, incoming, o, i));
        if (tempoLocked && (bothEnergetic || compatibility >= 60))
            candidates.AddIfNotNull(Rolling(outgoing, incoming, o, i));
        candidates.Add(Relaxed(outgoing, incoming, o, i, shortOnly: !tempoLocked));

        // First candidate without a vocal clash wins; if everything clashes, the shortest option
        // keeps the overlap brief.
        var clean = candidates.FirstOrDefault(c => !c.VocalClash);
        if (clean != null) return clean;

        var fallback = candidates.OrderBy(c => c.DurationBars).First();
        return fallback with { Reason = fallback.Reason + " · vocals overlap — kept short" };
    }

    /// <summary>
    /// The plan for one specific transition type (the Mix editor's Rolling / Relaxed / Drop Sync
    /// buttons), or null when the tracks can't do it (no drop, not enough intro, …).
    /// </summary>
    public static TransitionPlan? PlanKind(TrackStructure outgoing, TrackStructure incoming, PlannedTransitionKind kind)
    {
        var o = ComputeSections(outgoing);
        var i = ComputeSections(incoming);
        return kind switch
        {
            PlannedTransitionKind.DropSync => DropSync(outgoing, incoming, o, i),
            PlannedTransitionKind.Rolling => Rolling(outgoing, incoming, o, i),
            _ => Relaxed(outgoing, incoming, o, i, shortOnly: !TempoLocked(outgoing, incoming)),
        };
    }

    /// <summary>Playback tempo-matches up to 6 %, but beyond ~4 % the pitch shift is audible.</summary>
    private static bool TempoLocked(TrackStructure a, TrackStructure b) =>
        a.Bpm > 0 && b.Bpm > 0 && Math.Abs(a.Bpm - b.Bpm) / a.Bpm <= 0.04;

    public static PlannedTransitionKind? KindFromPresetName(string? presetName) => presetName switch
    {
        "Rolling" => PlannedTransitionKind.Rolling,
        "Drop Sync" => PlannedTransitionKind.DropSync,
        "Relaxed" => PlannedTransitionKind.Relaxed,
        _ => null,
    };

    private static TransitionPlan Relaxed(TrackStructure out_, TrackStructure in_, TrackSections o, TrackSections i, bool shortOnly)
    {
        double bar = o.BarSeconds;
        int bars = shortOnly ? 8 : QuantiseDown(Math.Min(o.OutroBars, i.IntroBars));
        double source = o.OutroStart;
        // Finish the mix by the end of the outgoing track.
        if (source + bars * bar > o.OutroEnd) source = Math.Max(0, o.OutroEnd - bars * bar);
        double target = i.IntroStart;
        return Build(PlannedTransitionKind.Relaxed, out_, in_, source, target, bars, bar,
            shortOnly ? $"Relaxed · {bars} bars (tempos {PctText(out_, in_)} apart)" : $"Relaxed · {bars} bars, outro into intro");
    }

    private static TransitionPlan? Rolling(TrackStructure out_, TrackStructure in_, TrackSections o, TrackSections i)
    {
        if (in_.Drop1 is not { } inDrop) return null;
        double bar = o.BarSeconds;
        // Half the window before the drop (incoming build under the outgoing main section),
        // half after (outgoing outro under the incoming drop).
        int half = QuantiseDown(Math.Min(Math.Min((inDrop - i.IntroStart) / bar, o.OutroBars), 16));
        if (half < 8) return null;
        double source = o.OutroStart - half * bar;
        double target = inDrop - half * bar;
        if (source < 0 || target < 0) return null;
        return Build(PlannedTransitionKind.Rolling, out_, in_, source, target, half * 2, bar,
            $"Rolling · drop lands as the outgoing main section ends ({half}+{half} bars)");
    }

    private static TransitionPlan? DropSync(TrackStructure out_, TrackStructure in_, TrackSections o, TrackSections i)
    {
        if (out_.Drop2 is not { } outDrop || in_.Drop1 is not { } inDrop) return null;
        double bar = o.BarSeconds;
        int half = QuantiseDown(Math.Min((inDrop - i.IntroStart) / bar, 16));
        if (half < 8) return null;
        double source = outDrop - half * bar;
        double target = inDrop - half * bar;
        // The outgoing track must still be playing through the second half of the window.
        if (source < 0 || target < 0 || outDrop + half * bar > o.OutroEnd) return null;
        return Build(PlannedTransitionKind.DropSync, out_, in_, source, target, half * 2, bar,
            $"Drop Sync · both drops land together ({half}+{half} bars)");
    }

    private static TransitionPlan Build(PlannedTransitionKind kind, TrackStructure out_, TrackStructure in_,
        double source, double target, int bars, double bar, string reason)
    {
        source = SnapToGrid(source, out_.FirstDownbeat, bar);
        target = Math.Max(0, SnapToGrid(target, in_.FirstDownbeat, BarSeconds(in_.Bpm)));
        double seconds = bars * bar;
        bool clash = VocalsOverlap(out_, source, seconds) && VocalsOverlap(in_, target, seconds * 0.5);
        return new TransitionPlan(kind, source, target, bars, seconds, clash, reason);
    }

    /// <summary>
    /// Vocals of a track are active somewhere in [start, start+length). Only the stored vocal
    /// range is known, so a track whose "vocals" span nearly all of it (a detector reading
    /// everything as voice) isn't treated as a clash source — that would block every mix.
    /// </summary>
    public static bool VocalsOverlap(TrackStructure t, double start, double length)
    {
        if (t.VocalStart is not { } vs || t.VocalEnd is not { } ve || ve <= vs) return false;
        if (t.DurationSeconds > 0 && (ve - vs) / t.DurationSeconds > 0.85) return false;
        return vs < start + length && ve > start;
    }

    /// <summary>Largest of 32/16/8 bars that fits in <paramref name="availableBars"/>; 8 when less.</summary>
    public static int QuantiseDown(double availableBars) =>
        PhraseLengths.FirstOrDefault(p => p <= availableBars + 0.01, 8);

    public static double BarSeconds(double bpm) => BeatsPerBar * 60.0 / (bpm > 0 ? bpm : 174.0);

    /// <summary>Nearest bar line on the track's grid.</summary>
    public static double SnapToGrid(double seconds, double firstDownbeat, double bar)
    {
        if (bar <= 0) return seconds;
        double n = Math.Round((seconds - firstDownbeat) / bar);
        return firstDownbeat + n * bar;
    }

    private static string PctText(TrackStructure a, TrackStructure b) =>
        a.Bpm > 0 ? $"{Math.Abs(a.Bpm - b.Bpm) / a.Bpm * 100:0}%" : "?";

    private static void AddIfNotNull<T>(this List<T> list, T? item) where T : class
    {
        if (item != null) list.Add(item);
    }
}
