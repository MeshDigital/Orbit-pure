using System;

namespace SLSKDONET.Events;

/// <summary>
/// Published when library metadata enrichment completes or makes significant progress.
/// </summary>
public class LibraryMetadataEnrichedEvent
{
    /// <summary>
    /// The number of tracks that were enriched.
    /// </summary>
    public int EnrichedCount { get; }

    public LibraryMetadataEnrichedEvent(int enrichedCount)
    {
        EnrichedCount = enrichedCount;
    }
}

public class SearchRequestedEvent
{
    public string Query { get; }
    public SearchRequestedEvent(string query)
    {
        Query = query;
    }
}
