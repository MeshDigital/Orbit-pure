using System.Collections.Generic;

namespace SLSKDONET.Services
{
    /// <summary>
    /// Global context for drag-and-drop operations.
    /// </summary>
    public static class DragContext
    {
        // Data format identifiers
        public const string QueueTrackFormat = "ORBIT_QueueTrack";
        public const string LibraryTrackFormat = "ORBIT_LibraryTrack";

        // Playlist folder tree: dragging a playlist card or folder node to reorganize the tree
        public const string PlaylistCardNodeFormat = "ORBIT_PlaylistCardNode";
        public const string PlaylistFolderNodeFormat = "ORBIT_PlaylistFolderNode";
        
        /// <summary>
        /// Temporary storage for drag data (fallback for platforms that don't support custom formats).
        /// </summary>
        public static object? Current { get; set; }
    }
}
