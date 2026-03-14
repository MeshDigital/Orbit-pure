using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models;

namespace SLSKDONET.Models
{
    /// <summary>
    /// Represents a smart playlist that dynamically filters tracks based on criteria
    /// </summary>
    public class SmartPlaylist
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = "🎵";
        public string Description { get; set; } = string.Empty;
        
        /// <summary>
        /// Filter function to apply to all tracks
        /// </summary>
        public Func<IEnumerable<PlaylistTrack>, IEnumerable<PlaylistTrack>> Filter { get; set; } = tracks => tracks;
        
        /// <summary>
        /// Sort function to apply after filtering
        /// </summary>
        public Func<IEnumerable<PlaylistTrack>, IEnumerable<PlaylistTrack>> Sort { get; set; } = tracks => tracks;
    }
}
