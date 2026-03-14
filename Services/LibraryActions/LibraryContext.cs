using System.Collections.Generic;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.LibraryActions;

/// <summary>
/// Context object passed to library actions containing current selection and state
/// </summary>
public class LibraryContext
{
    /// <summary>
    /// Currently selected playlist/album
    /// </summary>
    public PlaylistJob? SelectedPlaylist { get; set; }

    /// <summary>
    /// Currently selected tracks
    /// </summary>
    public List<PlaylistTrackViewModel> SelectedTracks { get; set; } = new();

    /// <summary>
    /// Reference to the LibraryViewModel for callbacks
    /// </summary>
    public LibraryViewModel? ViewModel { get; set; }
}
