using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SLSKDONET.Services.Sets;

/// <summary>
/// Per-track inputs for "Check clashes" (#205 P4.4): length, mix cues and the vocal span. Null is
/// unknown, never guessed.
/// </summary>
public sealed record TrackMixData(
    double? DurationSeconds,
    double? MixInSeconds,
    double? MixOutSeconds,
    double? OutroStartSeconds,
    double? VocalStartSeconds,
    double? VocalEndSeconds)
{
    public bool HasVocalSpan => VocalStartSeconds is double s && VocalEndSeconds is double e && e > s;

    /// <summary>True when the outgoing mix window can be placed (a mix-out/outro cue, or the length).</summary>
    public bool CanPlaceMixOut => MixOutSeconds is not null || OutroStartSeconds is not null || DurationSeconds is > 0;
}

/// <summary>One slot as the clash check sees it, in set order.</summary>
public sealed record ClashSlot(Guid SlotId, string Number, string TrackUniqueHash, string Title, string Artist, double? Bpm);

/// <summary>A clash on the transition into <see cref="ToSlotId"/>.</summary>
public sealed record SetClash(Guid FromSlotId, Guid ToSlotId, string Text);

/// <summary>What the check found and how much of the set it could actually check.</summary>
public sealed record SetClashResult(IReadOnlyList<SetClash> Clashes, int Transitions, int SectionChecked, int VocalChecked)
{
    /// <summary>One line for the strip: what was checked, and what couldn't be (missing data).</summary>
    public string Summary
    {
        get
        {
            if (Transitions == 0) return "Add at least two tracks to check transitions.";
            var found = Clashes.Count == 0 ? "No clashes" : Clashes.Count == 1 ? "1 clash" : $"{Clashes.Count} clashes";
            string Part(int n, string what, string missing) =>
                n == Transitions ? what : n == 0 ? $"{what}: {missing}" : $"{what} {n}/{Transitions}";
            return $"{found} · " + string.Join(" · ",
                Part(SectionChecked, "sections", "no phrase data"),
                Part(VocalChecked, "vocals", "no vocal data"),
                "artists");
        }
    }
}

/// <summary>
/// "Check clashes" (rebuild plan P4.4, #205): the on-demand sweep over every transition, beyond the
/// always-on BPM/key/energy warnings. Only checks what real data supports:
/// <list type="bullet">
/// <item>sections: the outgoing outro vs the incoming intro (the section fit the bridge finder uses);</item>
/// <item>vocals: both tracks have vocals inside the mix window (16 bars at the outgoing BPM, placed by
/// the MixOut/OutroStart and MixIn/Intro cues), only where both tracks have a vocal span;</item>
/// <item>artists: the same artist twice in a row.</item>
/// </list>
/// Pure: the Sets view model gathers the inputs.
/// </summary>
public static class SetClashCheck
{
    /// <summary>Outro→intro fit below this reads as "the sections sound very different".</summary>
    public const double WeakSectionFit = 0.5;
    public const int MixWindowBars = 16;

    /// <param name="slots">The set's slots in order.</param>
    /// <param name="mix">Mix data per track hash (missing = unknown).</param>
    /// <param name="sectionFit">Outro→intro fit 0–1 for a (from, to) hash pair; null without phrase data.</param>
    public static SetClashResult Run(
        IReadOnlyList<ClashSlot> slots,
        IReadOnlyDictionary<string, TrackMixData> mix,
        Func<string, string, double?> sectionFit)
    {
        var clashes = new List<SetClash>();
        int sectionChecked = 0, vocalChecked = 0;

        for (var i = 1; i < slots.Count; i++)
        {
            var a = slots[i - 1];
            var b = slots[i];
            string Prefix() => $"{a.Number} → {b.Number}: ";

            if (sectionFit(a.TrackUniqueHash, b.TrackUniqueHash) is double fit)
            {
                sectionChecked++;
                if (fit < WeakSectionFit)
                    clashes.Add(new SetClash(a.SlotId, b.SlotId,
                        Prefix() + $"outro and intro sound very different (fit {(100 * fit).ToString("0", CultureInfo.InvariantCulture)})"));
            }

            mix.TryGetValue(a.TrackUniqueHash, out var am);
            mix.TryGetValue(b.TrackUniqueHash, out var bm);
            if (am is { HasVocalSpan: true, CanPlaceMixOut: true } && bm is { HasVocalSpan: true } && a.Bpm is double bpm && bpm > 0)
            {
                vocalChecked++;
                if (VocalsOverlap(am, bm, bpm))
                    clashes.Add(new SetClash(a.SlotId, b.SlotId, Prefix() + "both tracks have vocals in the mix"));
            }

            if (a.Artist.Trim() is { Length: > 0 } artist
                && string.Equals(artist, b.Artist.Trim(), StringComparison.OrdinalIgnoreCase))
                clashes.Add(new SetClash(a.SlotId, b.SlotId, Prefix() + $"{artist} twice in a row"));
        }

        return new SetClashResult(clashes, Math.Max(0, slots.Count - 1), sectionChecked, vocalChecked);
    }

    /// <summary>
    /// The mix window: the outgoing track's last <see cref="MixWindowBars"/> bars before its MixOut cue
    /// (or from its OutroStart cue, else its end), against the incoming track's first bars from its
    /// MixIn/Intro cue (else its start).
    /// </summary>
    public static bool VocalsOverlap(TrackMixData outgoing, TrackMixData incoming, double outgoingBpm)
    {
        var window = MixWindowBars * 4 * 60.0 / outgoingBpm;

        double outStart, outEnd;
        if (outgoing.MixOutSeconds is double mixOut)
            (outStart, outEnd) = (mixOut - window, mixOut);
        else if (outgoing.OutroStartSeconds is double outro)
            (outStart, outEnd) = (outro, outgoing.DurationSeconds is double d ? Math.Min(outro + window, d) : outro + window);
        else if (outgoing.DurationSeconds is double duration && duration > 0)
            (outStart, outEnd) = (duration - window, duration);
        else
            return false; // nowhere to place the mix-out: not evidence of a clash

        var inStart = incoming.MixInSeconds ?? 0;
        var inEnd = inStart + window;

        var outgoingSings = outgoing.VocalEndSeconds > outStart && outgoing.VocalStartSeconds < outEnd;
        var incomingSings = incoming.VocalEndSeconds > inStart && incoming.VocalStartSeconds < inEnd;
        return outgoingSings && incomingSings;
    }
}
