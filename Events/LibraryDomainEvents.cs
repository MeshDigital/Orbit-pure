namespace SLSKDONET.Models;

// Library Service Events
public record LibraryEntryAddedEvent(LibraryEntry Entry);
public record LibraryEntryUpdatedEvent(LibraryEntry Entry);
public record LibraryEntryDeletedEvent(string UniqueHash);
public record LibraryMetadataEnrichedEvent(int Count);

/// <summary>
/// A playlist's track order was persisted (Reorder for better flow, Auto-Arrange, Automix,
/// Flow Builder "Save order", …). <see cref="LibraryService.SaveTrackOrderAsync"/> is the one
/// place that writes the order, so it is also the one place that publishes this — every surface
/// that renders a playlist (the Library grid, Cue Forge's docked browser, …) can re-read the
/// canonical order instead of keeping a stale snapshot.
/// </summary>
/// <param name="PlaylistId">The playlist whose order changed.</param>
/// <param name="OrderedPlaylistTrackIds">
/// Every row of that playlist in its new order (playlist-track ids, not track hashes).
/// </param>
public record PlaylistTrackOrderChangedEvent(Guid PlaylistId, IReadOnlyList<Guid> OrderedPlaylistTrackIds);

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
