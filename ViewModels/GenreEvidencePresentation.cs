using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;

namespace SLSKDONET.ViewModels;

/// <summary>
/// The Track Inspector's "Genre evidence" block (GA9 #254), built from <see cref="GenreEvidenceRecord"/>: one bar row per
/// layer (the library tag's trust, the audio model's buckets, the drums), and the line that says what cues use. Pure, so
/// old or odd rows are tested here rather than discovered in the UI.
/// </summary>
public sealed class GenreEvidencePresentation
{
    /// <summary>"Used for cues: Drum and Bass · from your tag".</summary>
    public string UsedForText { get; init; } = string.Empty;

    /// <summary>The resolver's conflict sentence, or null.</summary>
    public string? ConflictText { get; init; }
    public bool HasConflict => !string.IsNullOrEmpty(ConflictText);

    /// <summary>"Drum & Bass, Liquid · file tag": the library genre and who wrote it.</summary>
    public string? TagText { get; init; }
    public bool HasTag => !string.IsNullOrEmpty(TagText);

    /// <summary>One bar: how far that tag is trusted against the audio (by its source).</summary>
    public IReadOnlyList<GenreScore> TagBars { get; init; } = Array.Empty<GenreScore>();

    /// <summary>The audio model's buckets, as shares of the fused score, strongest first.</summary>
    public IReadOnlyList<GenreScore> AudioBars { get; init; } = Array.Empty<GenreScore>();
    public bool HasAudio => AudioBars.Count > 0;

    /// <summary>"Your library": the personal genre head's probabilities (GA11 #256), when it took part.</summary>
    public IReadOnlyList<GenreScore> PersonalBars { get; init; } = Array.Empty<GenreScore>();
    public bool HasPersonal => PersonalBars.Count > 0;

    /// <summary>Four-on-the-floor / backbeat / half-time strength.</summary>
    public IReadOnlyList<GenreScore> RhythmBars { get; init; } = Array.Empty<GenreScore>();
    public bool HasRhythm => RhythmBars.Count > 0;

    /// <summary>"87 / 174 BPM" — the two tempo octaves the drums were read on (mono font in the view).</summary>
    public string? TempoText { get; init; }

    public static GenreEvidencePresentation From(GenreEvidenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var display = string.IsNullOrWhiteSpace(record.Display) ? "no genre" : record.Display;
        var sub = !string.IsNullOrWhiteSpace(record.Subgenre) && !display.Contains(record.Subgenre, StringComparison.OrdinalIgnoreCase)
            ? $" / {record.Subgenre}" : string.Empty;
        var from = Enum.TryParse<GenreDecision>(record.Decision, out var decision) ? decision switch
        {
            GenreDecision.None => "no genre yet",
            GenreDecision.Audio => "from the audio",
            GenreDecision.Agreement => $"{SourceName(record.MetadataSource)} and audio agree",
            GenreDecision.MetadataDespiteConflict => $"from {SourceName(record.MetadataSource)} (the audio disagrees)",
            GenreDecision.AudioOverMetadata => $"from the audio ({SourceName(record.MetadataSource)} overruled)",
            _ => $"from {SourceName(record.MetadataSource)}",
        } : "from the audio";

        var tagBars = Array.Empty<GenreScore>();
        if (!string.IsNullOrWhiteSpace(record.MetadataText) && Enum.TryParse<GenreMetadataStrength>(record.MetadataStrength, out var strength)
            && strength != GenreMetadataStrength.None)
            tagBars = new[] { new GenreScore(SourceTitle(record.MetadataSource), TrustOf(strength)) };

        var rhythm = record.Rhythm is { Reliability: > 0 } r
            ? new[]
            {
                new GenreScore("4x4 kick", Clamp(r.FourOnFloor)),
                new GenreScore("Backbeat", Clamp(Math.Max(r.Backbeat, r.Break))),
                new GenreScore("Half-time", Clamp(r.HalfTime)),
            }
            : Array.Empty<GenreScore>();

        return new GenreEvidencePresentation
        {
            UsedForText = decision == GenreDecision.None && string.IsNullOrWhiteSpace(record.Display)
                ? "No genre for cues yet"
                : $"Used for cues: {display}{sub} · {from}",
            ConflictText = string.IsNullOrWhiteSpace(record.Conflict) ? null : record.Conflict,
            TagText = string.IsNullOrWhiteSpace(record.MetadataText) ? null : $"{record.MetadataText} · {SourceName(record.MetadataSource)}",
            TagBars = tagBars,
            AudioBars = Shares(record.AudioDistribution),
            PersonalBars = record.Personal is { Count: > 0 } personal
                ? personal.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(5)
                    .Select(kv => new GenreScore(kv.Key, Clamp(kv.Value))).ToArray()
                : Array.Empty<GenreScore>(),
            RhythmBars = rhythm,
            TempoText = record.Rhythm is { Reliability: > 0, TempoHigh: > 0 } t
                ? SLSKDONET.Utils.NumFormat.Ui($"{t.TempoLow:0} / {t.TempoHigh:0} BPM")
                : null,
        };
    }

    /// <summary>Fused bucket scores as shares of their total (they're not probabilities, so they can exceed 1).</summary>
    internal static IReadOnlyList<GenreScore> Shares(IReadOnlyDictionary<string, double>? distribution)
    {
        if (distribution is null || distribution.Count == 0) return Array.Empty<GenreScore>();
        var positive = distribution.Where(kv => kv.Value > 0 && !string.IsNullOrWhiteSpace(kv.Key)).ToList();
        var total = positive.Sum(kv => kv.Value);
        if (total <= 0) return Array.Empty<GenreScore>();
        return positive.OrderByDescending(kv => kv.Value).Take(5)
            .Select(kv => new GenreScore(kv.Key, kv.Value / total)).ToArray();
    }

    /// <summary>How far each tier is trusted against the audio (the resolver's base confidences).</summary>
    private static double TrustOf(GenreMetadataStrength strength) => strength switch
    {
        GenreMetadataStrength.Authoritative => 1.0,
        GenreMetadataStrength.Strong => 0.9,
        GenreMetadataStrength.FileTag => 0.85,
        GenreMetadataStrength.Medium => 0.75,
        GenreMetadataStrength.Weak => 0.5,
        _ => 0.25, // AudioDerived: the model again
    };

    private static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);

    /// <summary>"your tag", "file tag", "Beatport"… for running text.</summary>
    internal static string SourceName(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        null or "" => "the file tag",
        "user" => "your tag",
        "tags" => "the file tag",
        "maintenance" => "Library Maintenance",
        "beatport" => "Beatport",
        "discogs" => "Discogs",
        "musicbrainz" => "MusicBrainz",
        "spotify" => "Spotify",
        "deezer" => "Deezer",
        _ => source!,
    };

    /// <summary>Bar label for the tag row.</summary>
    private static string SourceTitle(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        null or "" or "tags" => "File tag",
        "user" => "Your tag",
        "maintenance" => "Maintenance",
        _ => SourceName(source),
    };
}
