namespace SLSKDONET.Models;

/// <summary>
/// Aggregate reliability counters for connection/search hardening telemetry.
/// </summary>
public sealed record NetworkReliabilityCounters(
    int KickedEventCount,
    int ExcludedPhraseQueryBlocks,
    int FilteredByFormatCount,
    int FilteredByBitrateCount,
    int FilteredBySampleRateCount,
    int FilteredByQueueCount,
    int FilteredByDedupCount,
    int FilteredByExcludedPhraseCount
);
