namespace SLSKDONET.Models;

/// <summary>Raised to open the Library Maintenance wizard overlay (Library ⋯ menu / Settings → Maintenance).</summary>
public record OpenLibraryMaintenanceRequestedEvent();

/// <summary>Raised to open the exact-duplicate review (Library ⋯ menu, Settings → Maintenance, activity chip).</summary>
public record OpenExactDuplicatesRequestedEvent();

/// <summary>
/// Progress of the audio content-hash backfill (issue #238). <paramref name="Done"/> counts every
/// entry looked at; <paramref name="Hashed"/> the ones that got a hash, <paramref name="Unreadable"/>
/// the files that exist but could not be hashed, <paramref name="Missing"/> the files not on disk.
/// </summary>
public record ContentHashBackfillProgressEvent(
    bool IsRunning, int Done, int Total, int Hashed, int Unreadable, int Missing, bool WasCancelled, string? Error);

/// <summary>Raised from the Track Inspector's "Use as genre" button; a service applies the track's top style as its genre.</summary>
public record UseAsGenreRequestedEvent(string TrackHash);
