using System;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Broad rhythmic family a track belongs to, for phrase/drop/BPM analysis purposes.
/// Breakbeat (DnB/Jungle) and four-on-the-floor (House/Techno/Trance/EDM) genres have
/// opposite rhythmic DNA — a syncopated, shifting breakbeat vs. a rigid quarter-note kick —
/// and need different BPM brackets, drop signals, and phrase granularity. See
/// <see cref="IGenreFamilyAnalysisStrategy"/> for where that difference is applied.
/// </summary>
public enum GenreFamily
{
    Unknown,
    Breakbeat,
    FourOnTheFloor,
    /// <summary>Dubstep / halftime / riddim: breakbeat-derived, but with different section lengths
    /// and a novelty-dominant drop signature.</summary>
    Dubstep
}

/// <summary>Four-on-the-floor sub-genre, used only for BPM bracket / phrase-tolerance selection.</summary>
public enum FourOnTheFloorSubgenre
{
    Unknown,
    House,
    TechHouseTechno,
    Trance
}

/// <summary>Where a genre label came from — model guesses are not strong enough to override
/// a contradictory DnB tempo, while a user/local tag is.</summary>
public enum GenreSource
{
    Unknown,
    UserTag,
    LocalFileTag,
    ModelInferred
}

/// <summary>Result of classifying a track into a <see cref="GenreFamily"/>.</summary>
public sealed record GenreFamilyResult(
    GenreFamily Family,
    FourOnTheFloorSubgenre Subgenre,
    double BpmBracketMin,
    double BpmBracketMax)
{
    /// <summary>True when a model-derived label was overridden because the tempo says Breakbeat.</summary>
    public bool WasTempoOverride { get; init; }
}

/// <summary>
/// Classifies a track into a <see cref="GenreFamily"/> from its genre-hint text and/or BPM.
/// Genre text is checked first (mirrors <c>PhraseAlignmentService.ResolvePreset</c>'s existing
/// substring-match style, for consistency); when genre text is empty or unrecognized, falls back
/// to a BPM-bracket-only match. Ambiguous/non-electronic tracks classify as <see cref="GenreFamily.Unknown"/>,
/// which every consumer must treat as "leave today's generic behavior alone" — this is the safety
/// net that keeps this whole feature additive rather than a behavior change for unrelated genres.
/// </summary>
public static class GenreFamilyClassifier
{
    public static GenreFamilyResult Classify(
        string? genreHint, float bpm, float? genreConfidence = null, GenreSource source = GenreSource.Unknown)
    {
        if (!string.IsNullOrWhiteSpace(genreHint))
        {
            var g = genreHint.ToLowerInvariant();

            bool explicitBass = g.Contains("dnb") || g.Contains("d&b") || g.Contains("drumandbass")
                || g.Contains("drum and bass") || g.Contains("drum & bass") || g.Contains("drumnbass")
                || g.Contains("jungle") || g.Contains("dubstep") || g.Contains("riddim")
                || g.Contains("halftime") || g.Contains("half-time");

            // Model-derived labels are not trustworthy for fast breakbeat. A 174-190 BPM track
            // tagged Techno/House/Ambient/Trance/EDM by the ONNX ensemble is Breakbeat; a user or
            // local-file tag still wins, and an explicit bass label is left alone.
            bool broadModelLabel = g.Contains("techn") || g.Contains("hous") || g.Contains("ambient")
                || g.Contains("trance") || g.Contains("edm") || g.Contains("electronic")
                || g.Contains("dance");
            if (source == GenreSource.ModelInferred
                && ((bpm >= 160 && bpm <= 190) || (bpm >= 85 && bpm <= 90))
                && !explicitBass && broadModelLabel)
            {
                return new GenreFamilyResult(GenreFamily.Breakbeat, FourOnTheFloorSubgenre.Unknown, 160, 190)
                {
                    WasTempoOverride = true,
                };
            }

            // A low-confidence label must not beat a strong contradictory BPM bracket. The
            // library's DnB tracks are routinely tagged "Techno"/"Ambient" at 0.1–0.3 confidence;
            // at 175 BPM the BPM bracket is the better evidence. Explicit bass-family text is
            // still trusted (a low-confidence "Dubstep" tag at 140 BPM should not become Trance).
            if (genreConfidence is < 0.5f && !explicitBass)
            {
                var bpmOnly = Classify(null, bpm);
                if (bpmOnly.Family != GenreFamily.Unknown) return bpmOnly;
            }

            // At DnB tempo the ONNX genre head is routinely wrong ("Techno"/"House" at 0.5-0.7
            // confidence on 174 BPM tracks). A confident label still wins; a weak one does not.
            if (genreConfidence is < 0.7f && bpm >= 160 && bpm <= 190)
            {
                var bpmOnly = Classify(null, bpm);
                if (bpmOnly.Family != GenreFamily.Unknown) return bpmOnly;
            }

            if (g.Contains("dubstep") || g.Contains("riddim") || g.Contains("halftime") || g.Contains("half-time"))
                return new GenreFamilyResult(GenreFamily.Dubstep, FourOnTheFloorSubgenre.Unknown, 138, 150);

            if (g.Contains("dnb") || g.Contains("d&b") || g.Contains("drumandbass")
                || g.Contains("drum and bass") || g.Contains("drum & bass")
                || g.Contains("drumnbass") || g.Contains("jungle"))
                return new GenreFamilyResult(GenreFamily.Breakbeat, FourOnTheFloorSubgenre.Unknown, 160, 190);

            if (g.Contains("trance") || g.Contains("uplifting"))
                return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.Trance, 135, 150);

            if (g.Contains("techhouse") || g.Contains("tech house") || g.Contains("techn"))
                return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.TechHouseTechno, 125, 138);

            if (g.Contains("hous"))
                return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.House, 118, 128);
        }

        // No usable genre text — fall back to BPM bracket alone. The 138-150 range is genuinely
        // ambiguous (trance or dubstep); without a tag we keep the existing trance default rather
        // than guessing dubstep from tempo.
        if (bpm >= 160 && bpm <= 190)
            return new GenreFamilyResult(GenreFamily.Breakbeat, FourOnTheFloorSubgenre.Unknown, 160, 190);
        // DnB half-time readings (e.g. 174 reported as 87).
        if (bpm >= 85 && bpm <= 90)
            return new GenreFamilyResult(GenreFamily.Breakbeat, FourOnTheFloorSubgenre.Unknown, 160, 190);
        if (bpm >= 118 && bpm <= 128)
            return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.House, 118, 128);
        if (bpm > 128 && bpm <= 138)
            return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.TechHouseTechno, 125, 138);
        if (bpm > 138 && bpm <= 150)
            return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.Trance, 135, 150);

        return new GenreFamilyResult(GenreFamily.Unknown, FourOnTheFloorSubgenre.Unknown, 0, 0);
    }

    /// <summary>
    /// Classifies from the three genre sources ORBIT already persists, preferring the curated /
    /// analysed fields over raw, messy tag lists: DetectedSubGenre → PrimaryGenre → Genres, then
    /// BPM fallback. Keeps the single-string overload as the implementation so existing callers
    /// keep their behaviour.
    /// </summary>
    public static GenreFamilyResult Classify(
        string? detectedSubGenre,
        string? primaryGenre,
        string? genres,
        float bpm,
        float? genreConfidence = null,
        GenreSource source = GenreSource.Unknown)
    {
        foreach (var hint in new[] { detectedSubGenre, primaryGenre, genres })
        {
            if (string.IsNullOrWhiteSpace(hint)) continue;
            var result = Classify(hint, bpm, genreConfidence, source);
            if (result.Family != GenreFamily.Unknown) return result;
        }

        return Classify(null, bpm);
    }
}
