namespace SLSKDONET.Models;

// Library Service Events
public record LibraryEntryAddedEvent(LibraryEntry Entry);
public record LibraryEntryUpdatedEvent(LibraryEntry Entry);
public record LibraryEntryDeletedEvent(string UniqueHash);
public record LibraryMetadataEnrichedEvent(int Count);

// File lifecycle events (download -> ingestion -> indexing)
public record FileIngestionQueuedEvent(
	string TrackUniqueHash,
	Guid PlaylistTrackId,
	string FilePath,
	DateTime QueuedAtUtc);

public record FileIngestionStartedEvent(
	string TrackUniqueHash,
	Guid PlaylistTrackId,
	string FilePath,
	DateTime StartedAtUtc);

public record FileIngestionCompletedEvent(
	string TrackUniqueHash,
	Guid PlaylistTrackId,
	string FilePath,
	DateTime CompletedAtUtc);

public record FileMissingDetectedEvent(
	string TrackUniqueHash,
	string FilePath,
	DateTime DetectedAtUtc,
	string Source);
