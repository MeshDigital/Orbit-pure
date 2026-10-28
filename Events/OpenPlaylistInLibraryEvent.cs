using System;

namespace SLSKDONET.Models;

/// <summary>
/// Navigate to the Library and select this playlist. Published by the Import page's
/// "Recent imports" list; handled by MainViewModel.
/// </summary>
public record OpenPlaylistInLibraryEvent(Guid PlaylistId);
