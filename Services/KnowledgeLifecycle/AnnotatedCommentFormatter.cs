using System.Globalization;
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
        // Invariant culture: the comment lands in Rekordbox/CDJ files, so a nl/de/fr machine must not
        // write "F:0,95".
        var inv = CultureInfo.InvariantCulture;
        var healthPart = health is null
            ? string.Empty
            : string.Format(inv, "[N:{0:F0} T:{1:F0} R:{2:F0}] ",
                health.NarrativeScore, health.TechnicalScore, health.PerformanceRiskScore);

        return string.Format(inv, "{0}C:{1} F:{2:F2}", healthPart, ConfidenceTier(confidence), freshness / 100.0);
    }
}
