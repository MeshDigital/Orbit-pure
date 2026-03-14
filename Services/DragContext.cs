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
        
        /// <summary>
        /// Temporary storage for drag data (fallback for platforms that don't support custom formats).
        /// </summary>
        public static object? Current { get; set; }
    }
}
