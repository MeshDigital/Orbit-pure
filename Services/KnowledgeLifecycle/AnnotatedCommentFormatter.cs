using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Slice K7d: dense Rekordbox <c>Comments</c> annotation for the knowledge-aware export. Keeps the
/// string short for CDJ comment columns: set health (<c>[N T R]</c>) + per-track confidence tier and
/// freshness. Example: <c>[N:85 T:90 R:12] C:High F:0.95</c>.
/// </summary>
public static class AnnotatedCommentFormatter
{
    /// <summary>Maps a confidence tier to a short, recognizable label.</summary>
    public static string ConfidenceTier(KnowledgeConfidenceLevel level) => level switch
    {
        KnowledgeConfidenceLevel.Confirmed => "High",
        KnowledgeConfidenceLevel.Likely => "Med",
        KnowledgeConfidenceLevel.Experimental => "Low",
        _ => "Unk",
    };

    /// <summary>
    /// Formats the full comment. <paramref name="health"/> may be null (no event profile / empty set),
    /// in which case only the track-level confidence + freshness are emitted. Freshness is normalised
    /// to 0–1.
    /// </summary>
    public static string FormatComment(
        SetHealthReport? health,
        KnowledgeConfidenceLevel confidence,
        double freshness)
    {
        var healthPart = health is null
            ? string.Empty
            : $"[N:{health.NarrativeScore:F0} T:{health.TechnicalScore:F0} R:{health.PerformanceRiskScore:F0}] ";

        return $"{healthPart}C:{ConfidenceTier(confidence)} F:{freshness / 100.0:F2}";
    }
}
