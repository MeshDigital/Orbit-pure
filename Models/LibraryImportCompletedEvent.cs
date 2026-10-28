using SLSKDONET.Services;

namespace SLSKDONET.Models;

/// <summary>
/// Published exactly once when a folder import ends (finished, cancelled or failed). The Library
/// refreshes on this one event instead of on every file, and the folder watcher replays file
/// events it held back while the import ran.
/// </summary>
public record LibraryImportCompletedEvent(LibraryImportResult Result);

/// <summary>
/// Published after each batch of LibraryEntries a folder import has committed (≈ every
/// <see cref="LibraryImportRequest.BatchSize"/> rows), so the Library can show the new rows while
/// the import is still running and a running enrichment can pick them up. Carries only hashes;
/// subscribers coalesce (the Library refreshes at most every couple of seconds).
/// </summary>
public record LibraryEntriesAddedEvent(System.Collections.Generic.IReadOnlyList<string> TrackHashes, LibraryImportTrigger Trigger);
