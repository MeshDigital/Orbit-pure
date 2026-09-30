using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;

namespace SLSKDONET.Engine.Cueing;

/// <summary>One cue of a drop group before it becomes an OrbitCue or CuePointEntity.</summary>
public readonly record struct DropGroupCue(double Timestamp, string Name, bool IsDrop, int SlotIndex, string Color, int BarsBefore);

/// <summary>
/// Cue templates and the standard layout for drops and their countdown ("build-in") cues —
/// anchored from the drop backward, the way electronic music is phrased. One definition shared
/// by automatic cue generation and both cue editors (Cue Forge, Flow Builder).
///
/// Templates (bars before the drop):
///   DnB / Bass      −16 −8   (also Hardstyle, Techno: 16-bar phrasing)
///   Long Blend      −32 −16  (House, Tech House, Trance: 32/64-bar cycles)
///   Quick Mix       −8 −4    (Hip-Hop, Pop, open format: 4/8-bar phrasing)
///   Custom          user-defined, e.g. "32,16,8"
///   Auto            picks one of the above from the track's genre
///
/// Layout (hot cue pads, CDJ/controller-friendly names, colours):
///   A B C  = Drop 1 group — [IN -16] [IN -8] [DROP 1]   orange / red-orange
///   D E F  = Drop 2 group — [IN -16] [IN -8] [DROP 2]   purple / cyan
///   G      = [OUT] mix-out (last drop + 32 bars)        red
///   H      = free for loops
///
/// Placing a drop by hand (<see cref="PlaceDrop"/>) does everything else: numbers the drops by
/// position, lays out their countdowns, keeps [OUT] after the last drop and removes the analysis'
/// auto cues. A countdown belongs to a drop while it sits exactly its stated bars before it, so
/// dragging a drop moves its countdowns along; one moved or renamed by hand stays put.
/// </summary>
public static class DropCountdownCues
{
    public const string Off = "Off";
    public const string Auto = "Auto";
    public const string DnB = "DnB / Bass (−16 −8)";
    public const string LongBlend = "Long Blend / House (−32 −16)";
    public const string QuickMix = "Quick Mix / Hip-Hop (−8 −4)";
    public const string Custom = "Custom";
    public const string DefaultCustomBars = "32,16,8";

    /// <summary>Choices offered in the editors and Settings (valid AppConfig.DropCountdownMode values).</summary>
    public static IReadOnlyList<string> Modes { get; } = new[] { Auto, DnB, LongBlend, QuickMix, Custom, Off };

    public const string OutName = "[OUT]";
    public static string DropName(int number) => $"[DROP {number}]";
    public static string CountdownName(int bars) => $"[IN -{bars}]";

    public const int OutPad = 6;          // G
    public const string OutColor = "#FF0000";

    /// <summary>[OUT] sits this many bars after the last drop (the end of its main section).</summary>
    public const int OutBarsAfterLastDrop = 32;

    // ── Templates ───────────────────────────────────────────────────────────────────────────

    /// <summary>Maps settings from earlier versions ("32,16,8", "16,8", …) to Custom with those
    /// bars, and unknown values to Auto.</summary>
    public static (string Mode, string CustomBars) Normalize(string? mode, string? customBars)
    {
        string custom = string.IsNullOrWhiteSpace(customBars) ? DefaultCustomBars : customBars.Trim();
        if (string.IsNullOrWhiteSpace(mode)) return (Auto, custom);
        if (Modes.Contains(mode)) return (mode, custom);
        if (ParseBars(mode).Length > 0) return (Custom, mode.Trim());
        return (Auto, custom);
    }

    /// <summary>Bars before the drop for <paramref name="mode"/>, largest first.</summary>
    public static IReadOnlyList<int> ResolveBars(string? mode, string? genre, double bpm, string? customBars = null)
    {
        var (m, custom) = Normalize(mode, customBars);
        return m switch
        {
            Off => Array.Empty<int>(),
            DnB => new[] { 16, 8 },
            LongBlend => new[] { 32, 16 },
            QuickMix => new[] { 8, 4 },
            Custom => ParseBars(custom),
            _ => ResolveBars(AutoTemplate(genre, bpm), genre, bpm),
        };
    }

    /// <summary>The template "Auto" picks for a genre (and for the tempo when the genre is unknown).</summary>
    public static string AutoTemplate(string? genre, double bpm)
    {
        string g = (genre ?? string.Empty).ToLowerInvariant();
        if (Regex.IsMatch(g, @"hip[\s-]?hop|\brap\b|r&b|\brnb\b|\bpop\b|reggaeton|dancehall|afrobeat|open[\s-]?format"))
            return QuickMix;
        if (g.Contains("hardstyle") || g.Contains("hardcore") || g.Contains("hard dance")
            || (g.Contains("techno") && !g.Contains("tech house")))
            return DnB;
        return GenreFamilyClassifier.Classify(genre, (float)bpm).Family == GenreFamily.FourOnTheFloor ? LongBlend : DnB;
    }

    private static int[] ParseBars(string text) => text
        .Split(new[] { ',', ' ', '/', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => int.TryParse(p.TrimStart('-', '−'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
        .Where(n => n is > 0 and <= 128)
        .Distinct()
        .OrderByDescending(n => n)
        .ToArray();

    // ── Layout ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Drop number from "[DROP 2]" or the older "Drop 2" (optionally with a suffix such as " ✓AI").</summary>
    public static int? DropNumber(string? name)
    {
        var m = Regex.Match(name ?? string.Empty, @"^\[?DROP (\d+)\]?", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    public static string DropColor(int number) => number switch { 1 => "#FF4500", 2 => "#00CCFF", _ => "#FF0000" };
    public static string CountdownColor(int dropNumber) => dropNumber switch { 1 => "#FFA500", 2 => "#8800FF", _ => "#FF6600" };

    /// <summary>Hot cue pad of drop <paramref name="number"/> (C / F), or -1 (memory cue) beyond Drop 2.</summary>
    public static int DropPad(int number) => number switch { 1 => 2, 2 => 5, _ => -1 };

    /// <summary>
    /// A drop and its countdowns laid out: names, pads and colours. Countdowns that would land
    /// before the start of the track are skipped. The two closest to the drop get the group's pads
    /// (A B / D E); further ones are memory cues.
    /// </summary>
    public static List<DropGroupCue> Layout(int dropNumber, double dropTime, IReadOnlyList<int> bars, double bpm)
    {
        var group = new List<DropGroupCue>();
        double bar = bpm > 0 ? 240.0 / bpm : 0;
        int dropPad = DropPad(dropNumber);
        var closestFirst = bars.Distinct().OrderBy(b => b).ToList();
        for (int i = 0; i < closestFirst.Count && bar > 0; i++)
        {
            double t = dropTime - closestFirst[i] * bar;
            if (t < -1e-6) continue;
            int pad = dropPad >= 0 && i < 2 ? dropPad - 1 - i : -1;
            group.Add(new DropGroupCue(Math.Max(0, t), CountdownName(closestFirst[i]), false, pad, CountdownColor(dropNumber), closestFirst[i]));
        }
        group.Add(new DropGroupCue(dropTime, DropName(dropNumber), true, dropPad, DropColor(dropNumber), 0));
        return group.OrderBy(c => c.Timestamp).ToList();
    }

    /// <summary>Where [OUT] goes: <see cref="OutBarsAfterLastDrop"/> bars after the last drop on the
    /// bar grid, leaving at least 16 bars to mix out; null when the track is too short.</summary>
    public static double? OutTime(double lastDrop, double bpm, double downbeat, double duration)
    {
        if (bpm <= 0) return null;
        double bar = 240.0 / bpm;
        double t = lastDrop + OutBarsAfterLastDrop * bar;
        if (duration > 0)
        {
            double latest = downbeat + Math.Floor((duration - 16 * bar - downbeat) / bar) * bar;
            t = Math.Min(t, latest);
        }
        t = downbeat + Math.Round((t - downbeat) / bar) * bar;
        return t > lastDrop + 4 * bar && (duration <= 0 || t < duration) ? t : null;
    }

    // ── Linking ─────────────────────────────────────────────────────────────────────────────

    private static readonly Regex CountdownPattern = new(@"^(?:\[IN -(\d+)\]|(\d+) Bars to .+)$", RegexOptions.Compiled);

    /// <summary>Bars-before-drop a countdown name states ("[IN -16]", or the older "16 Bars to Drop 1").</summary>
    public static int? CountdownBars(string? name)
    {
        var m = CountdownPattern.Match(name ?? string.Empty);
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>True when <paramref name="cue"/> is a countdown sitting exactly its stated number of
    /// bars before a drop at <paramref name="dropTime"/>.</summary>
    public static bool IsCountdownFor(OrbitCue cue, double dropTime, double bpm)
    {
        if (cue.Role != CueRole.Build || bpm <= 0 || CountdownBars(cue.Name) is not { } n) return false;
        double bar = 240.0 / bpm;
        return Math.Abs(cue.Timestamp - Math.Max(0, dropTime - n * bar)) <= bar / 8;
    }

    /// <summary>
    /// Rebuilds <paramref name="drop"/>'s countdowns for its current position (removing the ones
    /// linked to it at <paramref name="previousDropTime"/> when it just moved). A cue holding one of
    /// the group's pads becomes a memory cue. With no bars (template Off) or no tempo the list is
    /// returned unchanged.
    /// </summary>
    public static List<OrbitCue> Rebuild(IEnumerable<OrbitCue> cues, OrbitCue drop, IReadOnlyList<int> bars, double bpm,
        double? previousDropTime = null)
    {
        var list = cues.ToList();
        if (bars.Count == 0 || bpm <= 0 || drop.Role != CueRole.Drop) return list;

        double oldTime = previousDropTime ?? drop.Timestamp;
        list.RemoveAll(c => c != drop && (IsCountdownFor(c, oldTime, bpm) || IsCountdownFor(c, drop.Timestamp, bpm)));

        int number = DropNumber(drop.Name) ?? NumberAmongDrops(list, drop);
        foreach (var spec in Layout(number, drop.Timestamp, bars, bpm).Where(s => !s.IsDrop))
        {
            if (spec.SlotIndex >= 0) FreePad(list, spec.SlotIndex, drop);
            list.Add(new OrbitCue
            {
                Timestamp = spec.Timestamp, Name = spec.Name, Role = CueRole.Build, Color = spec.Color,
                Source = CueSource.User, SlotIndex = spec.SlotIndex, Confidence = 1.0,
            });
        }
        return list.OrderBy(c => c.Timestamp).ToList();
    }

    /// <summary>
    /// The one-click drop: puts a drop at <paramref name="time"/> and does the rest.
    /// <list type="bullet">
    /// <item>Which drop: <paramref name="number"/> when given (keys 1 / 2), otherwise an existing drop
    /// within 8 bars moves here, else a new drop is added (with two drops already, the nearer one moves).</item>
    /// <item>A cue already on that beat becomes the drop instead of a duplicate.</item>
    /// <item>The analysis' auto cues are removed — your cues take over the track.</item>
    /// <item>Drops are numbered by position (earliest = Drop 1) unless a number was given, and each
    /// gets its name, colour, pad and countdowns from <paramref name="bars"/>.</item>
    /// <item>[OUT] (pad G) follows the last drop, unless you moved or renamed it yourself.</item>
    /// </list>
    /// </summary>
    public static List<OrbitCue> PlaceDrop(IEnumerable<OrbitCue> cues, double time, int? number,
        IReadOnlyList<int> bars, double bpm, double downbeat, double duration, out OrbitCue drop)
    {
        var list = cues.ToList();
        double bar = bpm > 0 ? 240.0 / bpm : 1;
        double? outBefore = CurrentOutTime(list, bpm, downbeat, duration);
        var drops = Drops(list);

        OrbitCue? target = number is { } n
            ? drops.FirstOrDefault(d => DropNumber(d.Name) == n)
            : drops.FirstOrDefault(d => Math.Abs(d.Timestamp - time) <= 8 * bar)
              ?? (drops.Count >= 2 ? drops.OrderBy(d => Math.Abs(d.Timestamp - time)).First() : null);

        if (target != null)
        {
            list = RemoveFor(list, target.Timestamp, bpm);
            target.Timestamp = time;
        }
        else
        {
            target = list.FirstOrDefault(c => !c.IsLoop && c.Role != CueRole.Drop && Math.Abs(c.Timestamp - time) <= bar / 8);
            if (target == null)
            {
                target = new OrbitCue { Timestamp = time, Confidence = 1.0 };
                list.Add(target);
            }
            else target.Timestamp = time;
        }
        target.Role = CueRole.Drop;
        target.Source = CueSource.User;
        target.IsLoop = false;
        drop = target;

        // Your drop replaces the analysis' guesses.
        list = WithoutAutoCues(list, target);

        // A forced number takes that slot from whichever drop had it; otherwise number by position.
        if (number is { } forced)
        {
            foreach (var other in Drops(list).Where(d => d != target && DropNumber(d.Name) == forced).ToList())
            {
                list = RemoveFor(list, other.Timestamp, bpm);
                list.Remove(other);
            }
            list = Relabel(list, target, forced, bars, bpm);
        }
        else
        {
            int i = 0;
            foreach (var d in Drops(list)) list = Relabel(list, d, ++i, bars, bpm);
        }

        return UpdateOut(list, outBefore, bpm, downbeat, duration);
    }

    /// <summary>After a drop was moved or deleted: rebuilds the build-in cues of every drop (names,
    /// colours and pads of your drops are left as they are) and moves [OUT] with the last drop.</summary>
    public static List<OrbitCue> Refresh(IEnumerable<OrbitCue> cues, IReadOnlyList<int> bars, double bpm, double downbeat,
        double duration, double? outBefore)
    {
        var list = cues.ToList();
        foreach (var d in Drops(list)) list = Rebuild(list, d, bars, bpm);
        return UpdateOut(list, outBefore, bpm, downbeat, duration);
    }

    /// <summary>Where [OUT] would be for the current drops (to tell later whether it was moved by hand).</summary>
    public static double? CurrentOutTime(IEnumerable<OrbitCue> cues, double bpm, double downbeat, double duration)
    {
        var last = Drops(cues).LastOrDefault();
        return last == null ? null : OutTime(last.Timestamp, bpm, downbeat, duration);
    }

    private static List<OrbitCue> Relabel(List<OrbitCue> list, OrbitCue drop, int number, IReadOnlyList<int> bars, double bpm)
    {
        drop.Name = DropName(number);
        drop.Color = DropColor(number);
        int pad = DropPad(number);
        if (pad >= 0) FreePad(list, pad, drop);
        drop.SlotIndex = pad;
        return Rebuild(list, drop, bars, bpm);
    }

    /// <summary>[OUT] moves with the last drop while it's still where the drops put it; a missing one
    /// is created; one you moved or renamed is left alone.</summary>
    private static List<OrbitCue> UpdateOut(List<OrbitCue> list, double? outBefore, double bpm, double downbeat, double duration)
    {
        var target = CurrentOutTime(list, bpm, downbeat, duration);
        double tolerance = bpm > 0 ? 30.0 / bpm : 0.1;
        var existing = list.FirstOrDefault(c => c.Name == OutName && !c.IsLoop);
        if (target == null)
        {
            // No drops left: an [OUT] the drops placed goes with them.
            if (existing != null && outBefore is { } was && Math.Abs(existing.Timestamp - was) <= tolerance) list.Remove(existing);
            return list;
        }
        if (existing == null)
        {
            FreePad(list, OutPad, null);
            list.Add(new OrbitCue
            {
                Timestamp = target.Value, Name = OutName, Role = CueRole.Outro, Color = OutColor,
                Source = CueSource.User, SlotIndex = OutPad, Confidence = 1.0,
            });
        }
        else if (outBefore is { } before && Math.Abs(existing.Timestamp - before) <= tolerance)
        {
            existing.Timestamp = target.Value;
        }
        return list.OrderBy(c => c.Timestamp).ToList();
    }

    private static List<OrbitCue> Drops(IEnumerable<OrbitCue> cues) =>
        cues.Where(c => c.Role == CueRole.Drop && !c.IsLoop).OrderBy(c => c.Timestamp).ToList();

    private static void FreePad(List<OrbitCue> list, int pad, OrbitCue? owner)
    {
        foreach (var other in list.Where(c => c != owner && !c.IsLoop && c.SlotIndex == pad)) other.SlotIndex = -1;
    }

    private static int NumberAmongDrops(IEnumerable<OrbitCue> cues, OrbitCue drop)
    {
        int i = Drops(cues).IndexOf(drop);
        return i >= 0 ? i + 1 : 1;
    }

    /// <summary>Keeps <paramref name="keep"/> and every user cue (including countdowns); drops the
    /// analysis' auto cues. Undo in either editor brings them back.</summary>
    public static List<OrbitCue> WithoutAutoCues(IEnumerable<OrbitCue> cues, OrbitCue keep) =>
        cues.Where(c => c == keep || c.Source != CueSource.Auto).ToList();

    /// <summary>Removes the countdowns of a drop at <paramref name="dropTime"/> (when it's deleted, moved or stops being a drop).</summary>
    public static List<OrbitCue> RemoveFor(IEnumerable<OrbitCue> cues, double dropTime, double bpm) =>
        cues.Where(c => !IsCountdownFor(c, dropTime, bpm)).ToList();
}
