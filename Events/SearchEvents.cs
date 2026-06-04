using System.Collections.Generic;

namespace SLSKDONET.Models;

public record ExternalDiscoveryRequestedEvent(string TrackHash);
public record SearchHardCapTriggeredEvent(string Query, int HardResultCap, int HardFileCap, string Reason);
public record ExcludedSearchPhrasesUpdatedEvent(IReadOnlyCollection<string> Phrases, int AddedCount, int TotalCount);
