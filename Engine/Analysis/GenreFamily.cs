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
    FourOnTheFloor
}

/// <summary>Four-on-the-floor sub-genre, used only for BPM bracket / phrase-tolerance selection.</summary>
public enum FourOnTheFloorSubgenre
{
    Unknown,
    House,
    TechHouseTechno,
    Trance
}

/// <summary>Result of classifying a track into a <see cref="GenreFamily"/>.</summary>
public sealed record GenreFamilyResult(
    GenreFamily Family,
    FourOnTheFloorSubgenre Subgenre,
    double BpmBracketMin,
    double BpmBracketMax);

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
    public static GenreFamilyResult Classify(string? genreHint, float bpm)
    {
        if (!string.IsNullOrWhiteSpace(genreHint))
        {
            var g = genreHint.ToLowerInvariant();

            if (g.Contains("dnb") || g.Contains("drumandbass") || g.Contains("drum and bass") || g.Contains("jungle"))
                return new GenreFamilyResult(GenreFamily.Breakbeat, FourOnTheFloorSubgenre.Unknown, 170, 180);

            if (g.Contains("trance") || g.Contains("uplifting"))
                return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.Trance, 135, 150);

            if (g.Contains("techhouse") || g.Contains("tech house") || g.Contains("techn"))
                return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.TechHouseTechno, 125, 138);

            if (g.Contains("hous"))
                return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.House, 118, 128);
        }

        // No usable genre text — fall back to BPM bracket alone.
        if (bpm >= 170 && bpm <= 180)
            return new GenreFamilyResult(GenreFamily.Breakbeat, FourOnTheFloorSubgenre.Unknown, 170, 180);
        if (bpm >= 118 && bpm <= 128)
            return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.House, 118, 128);
        if (bpm > 128 && bpm <= 138)
            return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.TechHouseTechno, 125, 138);
        if (bpm > 138 && bpm <= 150)
            return new GenreFamilyResult(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.Trance, 135, 150);

        return new GenreFamilyResult(GenreFamily.Unknown, FourOnTheFloorSubgenre.Unknown, 0, 0);
    }
}
