using System;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Models;

/// <summary>
/// A lightweight feature snapshot for one structural section of a track
/// (Intro, Build, Drop, Breakdown, Outro, etc.).
///
/// Derived from <see cref="TrackPhraseEntity"/> rows combined with track-level
/// scalars from <see cref="AudioFeaturesEntity"/>. No additional DB schema required.
///
/// Used by the <see cref="Services.Similarity.SectionVectorService"/> and
/// <see cref="Services.Playlist.PlaylistOptimizer"/> for section-aware transition
/// matching — most critically: comparing the OUTRO profile of track A against the
/// INTRO profile of track B when picking the next track in a set.
/// </summary>
public sealed class SectionFeatureVector
{
    /// <summary>Structural type of this section (Intro, Build, Drop, Outro…).</summary>
    public PhraseType SectionType { get; init; }

    /// <summary>
    /// Average energy level of this section, 0–1.
    /// Sourced directly from <see cref="TrackPhraseEntity.EnergyLevel"/>.
    /// </summary>
    public float EnergyLevel { get; init; }

    /// <summary>
    /// Section start expressed as a fraction of total track duration (0 = start, 1 = end).
    /// Useful for detecting whether an "intro" genuinely opens the track or is mid-song.
    /// </summary>
    public float StartRatio { get; init; }

    /// <summary>Duration of this section as a fraction of total track duration (0–1).</summary>
    public float DurationRatio { get; init; }

    /// <summary>
    /// Track-level arousal (1–9 scale) normalised to 0–1 (value / 9).
    /// Provides overall intensity context when section energy is ambiguous.
    /// </summary>
    public float Arousal { get; init; }

    /// <summary>Track-level danceability (0–1).</summary>
    public float Danceability { get; init; }

    /// <summary>
    /// Spectral brightness (SpectralCentroid / 20 000 Hz reference), clamped 0–1.
    /// Higher = brighter / more treble-heavy section character.
    /// </summary>
    public float SpectralBrightness { get; init; }

    /// <summary>Phrase detection confidence (0–1).</summary>
    public float Confidence { get; init; }

    // ── Similarity helpers ────────────────────────────────────────────────

    /// <summary>
    /// Euclidean distance to <paramref name="other"/> in the 4-D feature space
    /// (EnergyLevel, Arousal, Danceability, SpectralBrightness).
    /// Lower = sections are more alike.
    /// Range: [0, 2] (max when all four dimensions are fully opposed).
    /// </summary>
    public double DistanceTo(SectionFeatureVector other)
    {
        double de = EnergyLevel       - other.EnergyLevel;
        double da = Arousal           - other.Arousal;
        double dd = Danceability      - other.Danceability;
        double ds = SpectralBrightness- other.SpectralBrightness;
        return Math.Sqrt(de * de + da * da + dd * dd + ds * ds);
    }

    /// <summary>
    /// Returns a value 0–1 representing how well the OUTRO of this section
    /// flows into <paramref name="introOfNextTrack"/>.
    /// 1.0 = perfect match. 0.0 = total mismatch.
    /// Only meaningful when called on an Outro section and passed an Intro section.
    /// </summary>
    public float TransitionScore(SectionFeatureVector introOfNextTrack)
    {
        // Max possible distance in 4-D unit hypercube is sqrt(4) = 2.
        const double maxDist = 2.0;
        double dist = DistanceTo(introOfNextTrack);
        return (float)Math.Clamp(1.0 - dist / maxDist, 0.0, 1.0);
    }
}
