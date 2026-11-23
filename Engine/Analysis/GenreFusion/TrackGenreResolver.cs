using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Engine.Analysis.GenreFusion;

/// <summary>A track's library genre (<c>LibraryEntry.Genres</c>) and who wrote it (the <c>MetadataProvenance</c>
/// "genre" row: <c>user</c>, <c>tags</c>, <c>maintenance</c> or a provider name; null when nobody recorded it,
/// which is how genres read from file tags at import arrive).</summary>
public sealed record TrackGenreContext(string? Genres, string? Source);

/// <summary>How far a library genre is trusted against the audio model, by who wrote it.</summary>
public enum GenreMetadataStrength
{
    /// <summary>No usable genre (empty or junk such as "Electronic").</summary>
    None,
    /// <summary>Written by Library Maintenance from the model ("Use as genre"): the audio again, not independent.</summary>
    AudioDerived,
    /// <summary>Artist- or album-level provider genres (Spotify, Deezer…): a vote only.</summary>
    Weak,
    /// <summary>MusicBrainz, or a genre of unknown origin (file tags imported before provenance existed).</summary>
    Medium,
    /// <summary>A clean file tag: whoever made or shared the file wrote it. It wins unless strong audio that the drums
    /// back disagrees (a 10-track online check found doubtful tags wrong about half the time).</summary>
    FileTag,
    /// <summary>Beatport (per track) or Discogs (per release).</summary>
    Strong,
    /// <summary>The user set it.</summary>
    Authoritative,
}

/// <summary>What decided the resolved genre.</summary>
public enum GenreDecision
{
    /// <summary>Neither the library nor the audio has a usable genre.</summary>
    None,
    /// <summary>No usable library genre: the audio model's opinion.</summary>
    Audio,
    /// <summary>The library genre, with no audio opinion to weigh it against or the audio was weaker.</summary>
    Metadata,
    /// <summary>The library genre and the audio agree on the bucket.</summary>
    Agreement,
    /// <summary>The library genre won although the audio strongly disagrees (see <see cref="ResolvedGenre.Conflict"/>).</summary>
    MetadataDespiteConflict,
    /// <summary>The audio won over a weaker library genre (see <see cref="ResolvedGenre.Conflict"/>).</summary>
    AudioOverMetadata,
}

/// <summary>The audio model's genre opinion, as persisted on <see cref="AudioFeaturesEntity"/> or fresh from fusion.</summary>
/// <param name="Label">Fused winner label ("Drum and Bass", "Hard Techno", "Trance"…).</param>
/// <param name="Confidence">The calibrated fused confidence (0–1; the calibration tops out near 0.69).</param>
/// <param name="FusionVersion">Fusion scoring version that produced the label; 0 when unknown (older rows).</param>
public sealed record AudioGenreOpinion(
    string? Label,
    float Confidence,
    RhythmSignature? Rhythm = null,
    IReadOnlyDictionary<string, double>? Distribution = null,
    IReadOnlyList<GenreGateEvidence>? Gate = null,
    int FusionVersion = 0,
    IReadOnlyDictionary<string, double>? Personal = null,
    string? PersonalModelId = null)
{
    /// <summary>The audio opinion stored on a features row. Gate factors and the fusion version come from the
    /// row's previous <see cref="AudioFeaturesEntity.GenreEvidenceJson"/>, which only analysis writes them into.</summary>
    public static AudioGenreOpinion FromFeatures(AudioFeaturesEntity f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var label = !string.IsNullOrWhiteSpace(f.DetectedSubGenre) ? f.DetectedSubGenre : f.ElectronicSubgenre;
        var confidence = f.SubGenreConfidence > 0 ? f.SubGenreConfidence : f.ElectronicSubgenreConfidence;
        var previous = GenreEvidenceRecord.TryParse(f.GenreEvidenceJson);
        return new AudioGenreOpinion(
            string.IsNullOrWhiteSpace(label) ? null : label,
            confidence,
            TryDeserialize<RhythmSignature>(f.RhythmSignatureJson),
            TryDeserialize<Dictionary<string, double>>(f.GenreDistributionJson),
            previous?.Gate,
            previous?.FusionVersion ?? 0,
            previous?.Personal,
            previous?.PersonalHead);
    }

    internal static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}" || json == "[]") return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>The genre ORBIT acts on (BPM bracket, phrase preset, drop weights, cue strategy) and why.</summary>
/// <param name="DisplayText">Canonical text (<see cref="GenreTaxonomy.CanonicalDisplay"/>) that the genre-text
/// matchers downstream understand: "Drum and Bass", "Tech House", "Trance"…</param>
/// <param name="Source">How far consumers may trust it over tempo (<see cref="GenreFamilyClassifier"/>).</param>
/// <param name="Conflict">Set when the library genre and a strong audio opinion disagree (shown by #254).</param>
public sealed record ResolvedGenre(
    GenreBucket Bucket,
    string? Subgenre,
    string DisplayText,
    float Confidence,
    GenreSource Source,
    GenreDecision Decision,
    string? MetadataSource,
    string? Conflict,
    GenreEvidenceRecord Evidence)
{
    public bool HasConflict => Conflict is not null;

    /// <summary>Family as the existing analysis strategies understand it (Other → Unknown).</summary>
    public GenreFamily Family => GenreTaxonomy.ToFamily(Bucket, Subgenre).Family;
}

/// <summary>
/// Metadata-first genre resolution (GA8 #253). Pure: the caller loads <see cref="TrackGenreContext"/>.
/// <para>Precedence: the user's genre always wins. A clean file tag, Beatport or Discogs wins too; when the audio
/// strongly disagrees and the drums agree with the audio, the tag is kept and a conflict is flagged, never
/// silently switched. MusicBrainz and genres of unknown origin lose to a strong audio opinion the drums don't
/// contradict. Spotify/Deezer (artist/album level) only decide when the audio is weak or missing. A genre that
/// Library Maintenance copied from the model counts as the audio, not as independent evidence.</para>
/// <para>Audio confidence here is the calibrated fused confidence (<see cref="GenreFusionEngine.Calibrate"/>),
/// whose top bin is about 0.69, so the thresholds sit on that scale.</para>
/// </summary>
public static class TrackGenreResolver
{
    /// <summary>Bumped whenever the precedence rules change, so the genre refresh (GA10) re-resolves stored evidence.
    /// 1 = GA8. 2 = file tags no longer outrank strong audio that the drums back.</summary>
    public const int ResolverVersion = 2;

    /// <summary>Calibrated audio confidence that counts as "strong" (raw fused ≥ ~0.9, ~59 % observed accuracy).</summary>
    public const float StrongAudioConfidence = 0.59f;

    /// <summary>Calibrated audio confidence at which a weak provider genre no longer decides.</summary>
    public const float WeakOverrideConfidence = 0.45f;

    private const float AuthoritativeConfidence = 1.0f;
    private const float StrongConfidence = 0.9f;
    private const float FileTagConfidence = 0.85f;
    private const float MediumConfidence = 0.75f;
    private const float WeakConfidence = 0.5f;

    /// <summary>Discogs genres with more distinct styles than this read as a compilation / VA release.</summary>
    private const int DiscogsMaxStyles = 3;

    private readonly record struct MetadataView(
        GenreBucket Bucket, string? Subgenre, GenreMetadataStrength Strength, bool Ambiguous, int Values);

    private readonly record struct AudioView(GenreBucket Bucket, string? Subgenre, float Confidence);

    public static ResolvedGenre Resolve(TrackGenreContext? metadata, AudioGenreOpinion? audio)
    {
        var meta = ReadMetadata(metadata);
        var sound = ReadAudio(audio);
        var rhythm = audio?.Rhythm;

        ResolvedGenre Make(GenreBucket bucket, string? sub, float confidence, GenreSource source,
            GenreDecision decision, string? conflict = null, bool? rhythmFavoursAudio = null)
        {
            var display = GenreTaxonomy.CanonicalDisplay(bucket, sub);
            var evidence = new GenreEvidenceRecord
            {
                FusionVersion = audio?.FusionVersion ?? 0,
                ResolverVersion = ResolverVersion,
                Bucket = bucket.ToString(),
                Subgenre = sub,
                Display = display,
                Confidence = MathF.Round(confidence, 3),
                Source = source.ToString(),
                Decision = decision.ToString(),
                Conflict = conflict,
                MetadataText = string.IsNullOrWhiteSpace(metadata?.Genres) ? null : metadata!.Genres!.Trim(),
                MetadataSource = metadata?.Source,
                MetadataStrength = meta?.Strength.ToString() ?? GenreMetadataStrength.None.ToString(),
                MetadataBucket = meta?.Bucket.ToString(),
                MetadataSubgenre = meta?.Subgenre,
                AudioLabel = audio?.Label,
                AudioBucket = sound?.Bucket.ToString(),
                AudioConfidence = sound is { } s ? MathF.Round(s.Confidence, 3) : null,
                AudioDistribution = audio?.Distribution,
                Rhythm = RhythmSummary.From(rhythm),
                RhythmFavoursAudio = rhythmFavoursAudio,
                Gate = audio?.Gate,
                Personal = audio?.Personal,
                PersonalHead = audio?.PersonalModelId,
            };
            return new ResolvedGenre(bucket, sub, display, confidence, source, decision,
                meta is null ? null : metadata?.Source, conflict, evidence);
        }

        // 1) No usable library genre: the audio decides.
        if (meta is not { } m)
        {
            return sound is { } a
                ? Make(a.Bucket, a.Subgenre, a.Confidence, GenreSource.ModelInferred, GenreDecision.Audio)
                : Make(GenreBucket.Other, null, 0f, GenreSource.Unknown, GenreDecision.None);
        }

        // 2) Model-derived library genre (Maintenance "Use as genre"): the audio, not a second opinion.
        if (m.Strength == GenreMetadataStrength.AudioDerived)
        {
            return sound is { } a
                ? Make(a.Bucket, a.Subgenre, a.Confidence, GenreSource.ModelInferred, GenreDecision.Audio)
                : Make(m.Bucket, m.Subgenre, WeakConfidence, GenreSource.ModelInferred, GenreDecision.Metadata);
        }

        var metaConfidence = BaseConfidence(m.Strength);
        var metaSource = SourceOf(m.Strength);

        // 3) No audio opinion: the library genre.
        if (sound is not { } audioView)
            return Make(m.Bucket, m.Subgenre, metaConfidence, metaSource, GenreDecision.Metadata);

        // 4) Same bucket: the library's subgenre is the finer one (tag "Liquid" + audio DnB → DnB / Liquid).
        if (m.Bucket == audioView.Bucket)
        {
            var confidence = Math.Min(1f, Math.Max(metaConfidence, audioView.Confidence) + 0.05f);
            return Make(m.Bucket, m.Subgenre ?? audioView.Subgenre, confidence, metaSource, GenreDecision.Agreement);
        }

        // 5) They disagree.
        var favours = RhythmFavoursAudio(audioView.Bucket, m.Bucket, rhythm);
        var strongAudio = audioView.Confidence >= StrongAudioConfidence;
        var conflict = $"Library genre says {GenreTaxonomy.CanonicalDisplay(m.Bucket, m.Subgenre)}, " +
                       $"the audio sounds like {GenreTaxonomy.CanonicalDisplay(audioView.Bucket, audioView.Subgenre)} " +
                       $"({audioView.Confidence:P0})";

        switch (m.Strength)
        {
            case GenreMetadataStrength.Authoritative:
                return Make(m.Bucket, m.Subgenre, metaConfidence, metaSource, GenreDecision.Metadata, rhythmFavoursAudio: favours);

            case GenreMetadataStrength.FileTag:
                // Only strong audio that the drums agree with overrides a file tag; without rhythm evidence the tag stays.
                return strongAudio && favours == true
                    ? Make(audioView.Bucket, audioView.Subgenre, audioView.Confidence, GenreSource.ModelInferred,
                        GenreDecision.AudioOverMetadata, conflict, favours)
                    : Make(m.Bucket, m.Subgenre, metaConfidence, metaSource, GenreDecision.Metadata, rhythmFavoursAudio: favours);

            case GenreMetadataStrength.Strong:
                return strongAudio && favours == true
                    ? Make(m.Bucket, m.Subgenre, metaConfidence, metaSource, GenreDecision.MetadataDespiteConflict, conflict, favours)
                    : Make(m.Bucket, m.Subgenre, metaConfidence, metaSource, GenreDecision.Metadata, rhythmFavoursAudio: favours);

            case GenreMetadataStrength.Medium:
                return strongAudio && favours != false
                    ? Make(audioView.Bucket, audioView.Subgenre, audioView.Confidence, GenreSource.ModelInferred,
                        GenreDecision.AudioOverMetadata, conflict, favours)
                    : Make(m.Bucket, m.Subgenre, metaConfidence, metaSource, GenreDecision.Metadata, rhythmFavoursAudio: favours);

            default: // Weak
                return audioView.Confidence >= WeakOverrideConfidence
                    ? Make(audioView.Bucket, audioView.Subgenre, audioView.Confidence, GenreSource.ModelInferred,
                        GenreDecision.Audio, rhythmFavoursAudio: favours)
                    : Make(m.Bucket, m.Subgenre, metaConfidence, metaSource, GenreDecision.Metadata, rhythmFavoursAudio: favours);
        }
    }

    /// <summary>Trust tier of a provenance source (see <see cref="GenreMetadataStrength"/>).</summary>
    public static GenreMetadataStrength StrengthOf(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "user" => GenreMetadataStrength.Authoritative,
        "tags" => GenreMetadataStrength.FileTag,
        "beatport" or "discogs" => GenreMetadataStrength.Strong,
        "musicbrainz" => GenreMetadataStrength.Medium,
        "maintenance" => GenreMetadataStrength.AudioDerived,
        // Unknown origin: genres read from file tags at import carry no provenance row (as do genres a very
        // old enrichment wrote), so they rank below a recorded tag.
        null or "" => GenreMetadataStrength.Medium,
        _ => GenreMetadataStrength.Weak, // spotify (artist-level), deezer (album-level), anything else
    };

    private static MetadataView? ReadMetadata(TrackGenreContext? context)
    {
        if (context is null) return null;
        var values = GenreTaxonomy.MapFreeTextAll(context.Genres).Where(v => !v.IsJunk && v.Weight > 0).ToList();
        if (values.Count == 0) return null;

        var primary = values[0];
        // The finer subgenre from any value in the same bucket ("Drum & Bass, Liquid" → Liquid).
        var subgenre = primary.Subgenre ?? values.Skip(1).FirstOrDefault(v => v.Bucket == primary.Bucket && v.Subgenre is not null).Subgenre;
        var clubBuckets = values.Where(v => v.Bucket != GenreBucket.Other).Select(v => v.Bucket).Distinct().Count();
        var ambiguous = clubBuckets > 1;

        var strength = StrengthOf(context.Source);
        if (strength == GenreMetadataStrength.Strong && string.Equals(context.Source?.Trim(), "discogs", StringComparison.OrdinalIgnoreCase)
            && values.Select(v => v.Subgenre ?? v.Bucket.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > DiscogsMaxStyles)
            strength = GenreMetadataStrength.Medium; // compilation / VA style soup
        // "Techno, House": the tag can't settle it on its own — drop a trust tier explicitly rather
        // than relying on enum ordering (which would break silently if a member were inserted).
        if (ambiguous)
            strength = strength switch
            {
                GenreMetadataStrength.Strong => GenreMetadataStrength.FileTag,
                GenreMetadataStrength.FileTag => GenreMetadataStrength.Medium,
                GenreMetadataStrength.Medium => GenreMetadataStrength.Weak,
                _ => strength,
            };

        return new MetadataView(primary.Bucket, subgenre, strength, ambiguous, values.Count);
    }

    private static AudioView? ReadAudio(AudioGenreOpinion? audio)
    {
        if (audio is null || string.IsNullOrWhiteSpace(audio.Label)) return null;
        var label = audio.Label.Trim();
        if (string.Equals(label, "Other", StringComparison.OrdinalIgnoreCase))
            return new AudioView(GenreBucket.Other, null, Math.Clamp(audio.Confidence, 0f, 1f));
        var mapped = GenreTaxonomy.MapFreeText(label);
        if (mapped.IsJunk || mapped.Weight <= 0) return null; // older rows: "Electronic", "Dance"…
        return new AudioView(mapped.Bucket, mapped.Subgenre, Math.Clamp(audio.Confidence, 0f, 1f));
    }

    /// <summary>
    /// Whether the drums back the audio bucket over the library's: both buckets are gated from an equal score
    /// and compared. Null without a reliable rhythm signature or when the gate is indifferent.
    /// </summary>
    internal static bool? RhythmFavoursAudio(GenreBucket audio, GenreBucket metadata, RhythmSignature? rhythm)
    {
        if (rhythm is null || rhythm.Reliability <= 0 || audio == metadata) return null;
        var scores = RhythmTempoGate.Apply(new Dictionary<GenreBucket, double> { [audio] = 0.5, [metadata] = 0.5 }, rhythm).Scores;
        var a = scores[audio];
        var m = scores[metadata];
        if (a > m * 1.05) return true;
        if (m > a * 1.05) return false;
        return null;
    }

    private static float BaseConfidence(GenreMetadataStrength strength) => strength switch
    {
        GenreMetadataStrength.Authoritative => AuthoritativeConfidence,
        GenreMetadataStrength.Strong => StrongConfidence,
        GenreMetadataStrength.FileTag => FileTagConfidence,
        GenreMetadataStrength.Medium => MediumConfidence,
        _ => WeakConfidence,
    };

    /// <summary>A trusted library genre beats a contradictory tempo in <see cref="GenreFamilyClassifier"/>;
    /// a weak provider genre is a guess like the model's.</summary>
    private static GenreSource SourceOf(GenreMetadataStrength strength) => strength switch
    {
        GenreMetadataStrength.Authoritative => GenreSource.UserTag,
        GenreMetadataStrength.Strong or GenreMetadataStrength.FileTag or GenreMetadataStrength.Medium => GenreSource.LocalFileTag,
        _ => GenreSource.ModelInferred,
    };
}

/// <summary>The rhythm signature, rounded for the evidence record.</summary>
public sealed record RhythmSummary(double FourOnFloor, double Backbeat, double HalfTime, double Break,
    double TempoLow, double TempoHigh, double Reliability)
{
    public static RhythmSummary? From(RhythmSignature? r) => r is null ? null : new(
        Math.Round(r.FourOnFloorScore, 3), Math.Round(r.BackbeatScore, 3), Math.Round(r.HalfTimeScore, 3),
        Math.Round(r.BreakIrregularity, 3), Math.Round(r.TempoLow, 2), Math.Round(r.TempoHigh, 2), Math.Round(r.Reliability, 3));
}

/// <summary>
/// What is persisted in <see cref="AudioFeaturesEntity.GenreEvidenceJson"/>: the resolved genre, the library
/// genre and its source, the audio opinion with its distribution, rhythm summary and gate factors, and any
/// conflict. <see cref="Version"/> is this record's own format version.
/// </summary>
public sealed class GenreEvidenceRecord
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int Version { get; init; } = CurrentVersion;
    public int FusionVersion { get; init; }
    /// <summary><see cref="TrackGenreResolver.ResolverVersion"/> that decided this row; 0 = before it was recorded.</summary>
    public int ResolverVersion { get; init; }

    public string Bucket { get; init; } = nameof(GenreBucket.Other);
    public string? Subgenre { get; init; }
    public string Display { get; init; } = string.Empty;
    public float Confidence { get; init; }
    public string Source { get; init; } = nameof(GenreSource.Unknown);
    public string Decision { get; init; } = nameof(GenreDecision.None);
    public string? Conflict { get; init; }

    public string? MetadataText { get; init; }
    public string? MetadataSource { get; init; }
    public string? MetadataStrength { get; init; }
    public string? MetadataBucket { get; init; }
    public string? MetadataSubgenre { get; init; }

    public string? AudioLabel { get; init; }
    public string? AudioBucket { get; init; }
    public float? AudioConfidence { get; init; }
    public IReadOnlyDictionary<string, double>? AudioDistribution { get; init; }

    public RhythmSummary? Rhythm { get; init; }
    public bool? RhythmFavoursAudio { get; init; }
    public IReadOnlyList<GenreGateEvidence>? Gate { get; init; }

    /// <summary>The personal genre head's probabilities by bucket display text (GA11 #256), when it took part.</summary>
    public IReadOnlyDictionary<string, double>? Personal { get; init; }
    /// <summary>Which trained head took part (<see cref="PersonalGenreModel.Id"/>), or null. GA10 compares it.</summary>
    public string? PersonalHead { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Null for empty or unreadable JSON.</summary>
    public static GenreEvidenceRecord? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<GenreEvidenceRecord>(json, Options); }
        catch (JsonException) { return null; }
    }
}
