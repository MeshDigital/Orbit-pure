using System;
using System.Globalization;
using System.Text.RegularExpressions;
using SLSKDONET.Utils;

namespace SLSKDONET.Models.EditLab;

/// <summary>Camelot-wheel maths for the Edit Lab's key matching.</summary>
public static class EditKeyMath
{
    private static readonly Regex CamelotPattern = new(@"^0?(1[0-2]|[1-9])([AB])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Normalises any key notation the library stores ("8A", "Am", "F#") to Camelot, or null.</summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var camelot = KeyConverter.ToCamelot(key.Trim());
        return TryParse(camelot, out var n, out var minor) ? Format(n, minor) : null;
    }

    public static bool TryParse(string? camelot, out int number, out bool minor)
    {
        number = 0; minor = false;
        if (string.IsNullOrWhiteSpace(camelot)) return false;
        var m = CamelotPattern.Match(camelot.Trim());
        if (!m.Success) return false;
        number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        minor = char.ToUpperInvariant(m.Groups[2].Value[0]) == 'A';
        return true;
    }

    public static string Format(int number, bool minor) => string.Create(CultureInfo.InvariantCulture, $"{number}{(minor ? 'A' : 'B')}");

    /// <summary>The key after shifting by <paramref name="semitones"/>. One semitone up is seven
    /// steps clockwise on the wheel (a fifth = one step, and 7 × 7 ≡ 1 mod 12).</summary>
    public static string? Transpose(string? camelot, int semitones)
    {
        if (!TryParse(Normalize(camelot), out var n, out var minor)) return null;
        int shifted = Mod(n - 1 + 7 * semitones, 12) + 1;
        return Format(shifted, minor);
    }

    /// <summary>Same key, relative major/minor, or one step either way on the wheel.</summary>
    public static bool IsCompatible(string? a, string? b) =>
        KeyConverter.GetHarmonicRelation(Normalize(a), Normalize(b)) != HarmonicRelation.None;

    /// <summary>
    /// Smallest semitone shift (−6..+6) that makes <paramref name="clipKey"/> mix harmonically with
    /// <paramref name="projectKey"/>. On a tie an exact key match wins, then shifting down (less
    /// "chipmunk" on vocals). Null when either key is unknown.
    /// </summary>
    public static int? SuggestShift(string? clipKey, string? projectKey)
    {
        var clip = Normalize(clipKey);
        var target = Normalize(projectKey);
        if (clip == null || target == null) return null;
        if (IsCompatible(clip, target)) return 0;
        for (int size = 1; size <= 6; size++)
        {
            int? best = null;
            foreach (var s in new[] { -size, size })
            {
                var shifted = Transpose(clip, s);
                if (!IsCompatible(shifted, target)) continue;
                if (string.Equals(shifted, target, StringComparison.Ordinal)) return s;
                best ??= s;
            }
            if (best != null) return best;
        }
        return null;
    }

    private static int Mod(int x, int m) => ((x % m) + m) % m;
}

/// <summary>How a clip plays once the project's matching rules are applied.</summary>
public readonly record struct ClipMatch(
    double Speed,
    double EffectiveSourceBpm,
    bool FollowsTempo,
    int Semitones,
    int? SuggestedSemitones,
    string? SourceKey,
    string? ResultKey)
{
    /// <summary>Stretch in percent (+4.2 = 4.2 % faster).</summary>
    public double StretchPercent => (Speed - 1.0) * 100.0;

    /// <summary>True beyond ±<see cref="EditMatching.StretchWarningPercent"/>, where the SMB
    /// time-stretch starts to smear transients audibly.</summary>
    public bool StretchWarning => Math.Abs(StretchPercent) > EditMatching.StretchWarningPercent;

    /// <summary>Total pitch factor for the shifter: undo the speed's pitch change (key-lock), then shift.</summary>
    public double PitchFactor => Math.Pow(2.0, Semitones / 12.0) / Speed;
}

/// <summary>Tempo/key matching and bar ↔ seconds maths for the Edit Lab.</summary>
public static class EditMatching
{
    /// <summary>Stretch beyond this (percent) is flagged as a quality risk.</summary>
    public const double StretchWarningPercent = 8.0;

    public static double SecondsPerBar(double bpm, int beatsPerBar = 4) =>
        bpm > 0 ? beatsPerBar * 60.0 / bpm : 2.0;

    public static double SecondsPerBar(EditProject project) => SecondsPerBar(project.Bpm, project.BeatsPerBar);

    public static double BarToSeconds(EditProject project, double bar) => bar * SecondsPerBar(project);

    public static double SecondsToBar(EditProject project, double seconds) => seconds / SecondsPerBar(project);

    /// <summary>Source seconds at a 1-based source bar (bar 1 = the first downbeat).</summary>
    public static double SourceBarToSeconds(EditSource source, double bar1Based, int beatsPerBar = 4) =>
        source.Bpm > 0
            ? source.DownbeatSeconds + (bar1Based - 1.0) * SecondsPerBar(source.Bpm, beatsPerBar)
            : (bar1Based - 1.0) * 2.0;

    /// <summary>1-based source bar at a source position.</summary>
    public static double SourceSecondsToBar(EditSource source, double seconds, int beatsPerBar = 4) =>
        source.Bpm > 0
            ? (seconds - source.DownbeatSeconds) / SecondsPerBar(source.Bpm, beatsPerBar) + 1.0
            : seconds / 2.0 + 1.0;

    /// <summary>The octave of <paramref name="sourceBpm"/> (×½, ×1, ×2…) closest to the project
    /// tempo, so an 87 BPM half-time read of a 174 track isn't stretched 100 %.</summary>
    public static double FoldTempo(double sourceBpm, double projectBpm)
    {
        if (sourceBpm <= 0 || projectBpm <= 0) return sourceBpm;
        double best = sourceBpm;
        double bestDistance = double.MaxValue;
        for (int k = -2; k <= 2; k++)
        {
            double candidate = sourceBpm * Math.Pow(2, k);
            double distance = Math.Abs(Math.Log(candidate / projectBpm));
            if (distance < bestDistance) { bestDistance = distance; best = candidate; }
        }
        return best;
    }

    public static bool FollowsTempo(EditProject project, EditClip clip) => clip.TempoMode switch
    {
        ClipTempoMode.Follow => true,
        ClipTempoMode.Native => false,
        _ => project.AutoMatchTempo,
    };

    public static ClipMatch Resolve(EditProject project, EditClip clip, EditSource? source)
    {
        double sourceBpm = source?.Bpm ?? 0;
        bool follows = FollowsTempo(project, clip) && sourceBpm > 0 && project.Bpm > 0;
        double effective = follows ? FoldTempo(sourceBpm, project.Bpm) : sourceBpm;
        double speed = follows ? project.Bpm / effective : 1.0;

        string? sourceKey = EditKeyMath.Normalize(source?.Key);
        int? suggested = EditKeyMath.SuggestShift(sourceKey, project.Key);
        int semitones = clip.KeyMode == ClipKeyMode.Manual
            ? Math.Clamp(clip.ManualSemitones, -12, 12)
            : project.AutoMatchKey ? suggested ?? 0 : 0;
        string? resultKey = sourceKey == null ? null : EditKeyMath.Transpose(sourceKey, semitones);
        return new ClipMatch(speed, effective, follows, semitones, suggested, sourceKey, resultKey);
    }

    /// <summary>How many source seconds a clip consumes.</summary>
    public static double SourceSecondsUsed(EditProject project, EditClip clip, ClipMatch match) =>
        clip.LengthBars * SecondsPerBar(project) * match.Speed;

    /// <summary>Timeline bars a stretch of source audio occupies once matched.</summary>
    public static double SourceSecondsToTimelineBars(EditProject project, double sourceSeconds, ClipMatch match) =>
        sourceSeconds / match.Speed / SecondsPerBar(project);

    public static string FormatSemitones(int semitones) =>
        semitones == 0 ? "0 st" : string.Create(CultureInfo.InvariantCulture, $"{semitones:+0;-0} st");

    public static string FormatStretch(double percent) =>
        Math.Abs(percent) < 0.05 ? "0.0 %" : string.Create(CultureInfo.InvariantCulture, $"{percent:+0.0;-0.0} %");
}
