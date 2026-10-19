using SLSKDONET.Services;

namespace SLSKDONET.Models;

/// <summary>
/// Published exactly once when a folder import ends (finished, cancelled or failed). The Library
/// refreshes on this one event instead of on every file, and the folder watcher replays file
/// events it held back while the import ran.
/// </summary>
public record LibraryImportCompletedEvent(LibraryImportResult Result);
