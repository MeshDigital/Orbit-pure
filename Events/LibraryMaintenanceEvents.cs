namespace SLSKDONET.Models;

/// <summary>Raised to open the Library Maintenance wizard overlay (Library ⋯ menu / Settings → Maintenance).</summary>
public record OpenLibraryMaintenanceRequestedEvent();

/// <summary>Raised from the Track Inspector's "Use as genre" button; a service applies the track's top style as its genre.</summary>
public record UseAsGenreRequestedEvent(string TrackHash);
