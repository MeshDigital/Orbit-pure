using System;

namespace SLSKDONET.Models
{
    public class SetlistTrackItem
    {
        public int Index { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string KeyDisplay { get; set; } = "";
        public string BpmDisplay { get; set; } = "";
        public string EnergyLevel { get; set; } = "";
        public bool IsSelected { get; set; }
        public Guid TrackId { get; set; }
        public PlaylistTrack? Track { get; set; }
        
        // Sprint 3: Intelligence properties
        public string Key { get; set; } = "";
        public double Energy { get; set; }
        public double VocalProbability { get; set; }
        
        // Sprint 4: Ghost Item support
        public bool IsGhost { get; set; }
    }

    public abstract class SetHealthIssue { }

    public class KeyClashIssue : SetHealthIssue
    {
        public string TrackA { get; set; } = "";
        public string TrackB { get; set; } = "";
        public string Description { get; set; } = "";
        public int TransitionIndex { get; set; }
    }

    public class EnergyGapIssue : SetHealthIssue
    {
        public int TrackIndex { get; set; }
        public string Description { get; set; } = "";
        public double FromEnergy { get; set; }
        public double ToEnergy { get; set; }
    }

    public class VocalClashIssue : SetHealthIssue
    {
        public string TrackA { get; set; } = "";
        public string TrackB { get; set; } = "";
        public int TransitionIndex { get; set; }
        public string Description { get; set; } = "";
    }
}
