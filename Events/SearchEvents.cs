using System;
using System.Collections.Generic;

namespace SLSKDONET.Models;

public record ExternalDiscoveryRequestedEvent(string TrackHash);
public record SearchHardCapTriggeredEvent(string Query, int HardResultCap, int HardFileCap, string Reason);
public record ExcludedSearchPhrasesUpdatedEvent(IReadOnlyCollection<string> Phrases, int AddedCount, int TotalCount);

/// <summary>
/// Fired when the Soulseek server sends a global ban message (e.g. "You have been banned for 30 minutes").
/// Consumers must pause all search activity until <see cref="LockoutUntilUtc"/>.
/// </summary>
public record SearchBanDetectedEvent(string RawMessage, DateTime LockoutUntilUtc);
