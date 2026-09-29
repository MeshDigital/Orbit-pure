using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;

namespace SLSKDONET.Engine.Cueing;

/// <summary>
/// Countdown cues tied to a Drop cue: "32 Bars to Drop 1", "16 Bars to Drop 1", "8 Bars to Drop 1"
/// placed exactly that many bars before it. Whenever a cue becomes a Drop, or a Drop is moved or
/// renamed, its countdowns are rebuilt so they always land on the right bar — the DJ only has to
/// place the drop itself.
///
/// A countdown is recognised purely by its role (Build) and name ("{n} Bars to {drop name}"), so
/// hand-placed cues with other names are never touched. Used by both cue editors (Cue Forge and the
/// Flow Builder transition editor).
/// </summary>
public static class DropCountdownCues
{
    public const string Off = "Off";
    public const string Auto = "Auto";

    /// <summary>Choices offered in the editors (and valid values of AppConfig.DropCountdownMode).</summary>
    public static IReadOnlyList<string> Modes { get; } = new[] { Auto, "32,16,8", "16,8", "32,16", "8", Off };

    /// <summary>Human label for a mode.</summary>
    public static string Describe(string mode) => mode switch
    {
        Auto => "Auto (by genre)",
        Off => "Off",
        _ => string.Join(" / ", ParseBars(mode).Select(b => $"-{b}")) + " bars",
    };

    /// <summary>Bars before the drop for <paramref name="mode"/>, largest first. "Auto": breakbeat
    /// (DnB, jungle, dubstep) builds in longer runs, so 32/16/8; four-on-the-floor 32/16.</summary>
    public static IReadOnlyList<int> ResolveBars(string? mode, string? genre, double bpm)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode == Off) return Array.Empty<int>();
        if (mode == Auto)
        {
            return GenreFamilyClassifier.Classify(genre, (float)bpm).Family == GenreFamily.FourOnTheFloor
                ? new[] { 32, 16 }
                : new[] { 32, 16, 8 };
        }
        return ParseBars(mode);
    }

    private static int[] ParseBars(string mode) => mode
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => int.TryParse(p, out var n) ? n : 0)
        .Where(n => n > 0)
        .Distinct()
        .OrderByDescending(n => n)
        .ToArray();

    private static readonly Regex CountdownName = new(@"^(\d+) Bars to (.+)$", RegexOptions.Compiled);

    /// <summary>True when <paramref name="cue"/> is one of the countdowns belonging to a drop named
    /// <paramref name="dropName"/>.</summary>
    public static bool IsCountdownFor(OrbitCue cue, string dropName)
    {
        if (cue.Role != CueRole.Build) return false;
        var m = CountdownName.Match(cue.Name ?? string.Empty);
        return m.Success && string.Equals(m.Groups[2].Value, dropName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns <paramref name="cues"/> with <paramref name="drop"/>'s countdowns rebuilt: existing ones
    /// (named after its current or <paramref name="previousDropName"/>) are removed and fresh ones added
    /// <c>bars × 4</c> beats before the drop. Countdowns that would fall before the start of the track
    /// are skipped. Each gets a free hot-cue pad if one is left, otherwise it's a memory cue.
    /// With no bars (mode Off) or no tempo the list is returned unchanged.
    /// </summary>
    public static List<OrbitCue> Rebuild(IEnumerable<OrbitCue> cues, OrbitCue drop, IReadOnlyList<int> bars, double bpm,
        string? previousDropName = null)
    {
        var list = cues.ToList();
        if (bars.Count == 0 || bpm <= 0 || drop.Role != CueRole.Drop) return list;

        list.RemoveAll(c => c != drop && (IsCountdownFor(c, drop.Name) ||
                                          (previousDropName != null && IsCountdownFor(c, previousDropName))));

        double barSeconds = 240.0 / bpm;
        foreach (var n in bars)
        {
            double t = drop.Timestamp - n * barSeconds;
            if (t < 0) continue;
            int freePad = Enumerable.Range(0, 8).FirstOrDefault(i => list.All(c => c.IsLoop || c.SlotIndex != i), -1);
            list.Add(new OrbitCue
            {
                Timestamp = t,
                Name = $"{n} Bars to {drop.Name}",
                Role = CueRole.Build,
                Color = "#FF6600", // Rekordbox orange, same as CueForgeViewModel.RekordboxColorForRole(Build)
                Source = CueSource.User,
                SlotIndex = freePad,
                Confidence = 1.0,
            });
        }
        return list.OrderBy(c => c.Timestamp).ToList();
    }

    /// <summary>
    /// Once the DJ places a drop themselves, the analysis' own guesses (auto cues) only clutter the
    /// waveform: keeps <paramref name="drop"/> and every user cue (including countdowns), drops the
    /// rest. Undo in either editor brings them back.
    /// </summary>
    public static List<OrbitCue> WithoutAutoCues(IEnumerable<OrbitCue> cues, OrbitCue drop) =>
        cues.Where(c => c == drop || c.Source != CueSource.Auto).ToList();

    /// <summary>Removes a deleted drop's countdowns.</summary>
    public static List<OrbitCue> RemoveFor(IEnumerable<OrbitCue> cues, string dropName) =>
        cues.Where(c => !IsCountdownFor(c, dropName)).ToList();
}
