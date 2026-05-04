using System;
using System.Linq;
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

    /// <summary>
    /// Optional section-specific embedding generated from the local phrase window.
    /// When present this is preferred over the old scalar-only heuristic.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Cached L2 norm of <see cref="Embedding"/> for faster cosine similarity.
    /// </summary>
    public float EmbeddingMagnitude { get; init; }

    public bool HasEmbedding => Embedding is { Length: > 0 };

    // ── Similarity helpers ────────────────────────────────────────────────

    /// <summary>
    /// Distance to <paramref name="other"/> in the blended section space.
    /// Falls back to the original 4-D heuristic when persisted embeddings are unavailable.
    /// Lower = sections are more alike.
    /// Range remains approximately [0, 2].
    /// </summary>
    public double DistanceTo(SectionFeatureVector other)
    {
        double de = EnergyLevel        - other.EnergyLevel;
        double da = Arousal            - other.Arousal;
        double dd = Danceability       - other.Danceability;
        double ds = SpectralBrightness - other.SpectralBrightness;
        double scalarDistance = Math.Sqrt(de * de + da * da + dd * dd + ds * ds);

        if (HasEmbedding && other.HasEmbedding && Embedding!.Length == other.Embedding!.Length)
        {
            double cosine = CosineSimilarity(Embedding!, other.Embedding!, EmbeddingMagnitude, other.EmbeddingMagnitude);
            double embeddingDistance = 1.0 - Math.Clamp(cosine, -1.0, 1.0);
            return (scalarDistance * 0.45) + (embeddingDistance * 0.55);
        }

        return scalarDistance;
    }

    private static double CosineSimilarity(float[] a, float[] b, float aMagnitudeHint, float bMagnitudeHint)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0.0;

        double dot = 0.0;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];

        double magA = aMagnitudeHint > 0 ? aMagnitudeHint : Math.Sqrt(a.Sum(v => v * v));
        double magB = bMagnitudeHint > 0 ? bMagnitudeHint : Math.Sqrt(b.Sum(v => v * v));
        if (magA <= 0 || magB <= 0)
            return 0.0;

        return dot / (magA * magB);
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
