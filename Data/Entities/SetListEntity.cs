using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities
{
    /// <summary>
    /// Represents a DJ transition archetype.
    /// </summary>
    public enum TransitionArchetype
    {
        DropSwap,
        LongBlend,
        QuickCut,
        VocalToInstrumental,
        EnergyReset,
        BuildToDrop,
        Custom
    }

    /// <summary>
    /// Phase 3: Set-Prep Intelligence.
    /// Represents a saved set-list with intelligent flow metadata.
    /// </summary>
    public class SetListEntity
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Overall flow continuity score (0-1.0).
        /// Calculated based on energy, key, and vocal transitions.
        /// </summary>
        public double FlowHealth { get; set; }

        /// <summary>
        /// Tunable flow weights for this specific set.
        /// Stored as JSON (FlowWeightSettings).
        /// </summary>
        public string? FlowWeightsJson { get; set; }

        public virtual ICollection<SetTrackEntity> Tracks { get; set; } = new List<SetTrackEntity>();
    }

    /// <summary>
    /// Represents a track within a SetList, including its transition relative to the previous track.
    /// </summary>
    public class SetTrackEntity
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [ForeignKey(nameof(SetList))]
        public Guid SetListId { get; set; }
        public virtual SetListEntity? SetList { get; set; }

        /// <summary>
        /// Links to the global track features and metadata via UniqueHash.
        /// </summary>
        public string TrackUniqueHash { get; set; } = string.Empty;

        /// <summary>
        /// Reference to the LibraryEntryEntity for full track data (Phase 6: rescue track application).
        /// </summary>
        [ForeignKey(nameof(Library))]
        public Guid? LibraryId { get; set; }
        public virtual LibraryEntryEntity? Library { get; set; }

        public int Position { get; set; }

        // Transition Metadata
        public TransitionArchetype TransitionType { get; set; } = TransitionArchetype.LongBlend;
        public double ManualOffset { get; set; }
        public string? TransitionReasoning { get; set; } // Forensic log for this specific transition

        /// <summary>
        /// Performance notes or reminders for the DJ at this transition.
        /// </summary>
        public string? DjNotes { get; set; }

        /// <summary>
        /// Phase 6: Track if this is a rescue track applied by stress-test.
        /// </summary>
        public bool IsRescueTrack { get; set; } = false;

        /// <summary>
        /// Phase 6: Reason why this track was added as a rescue (e.g., "Bridge: Energy Plateau").
        /// </summary>
        public string? RescueReason { get; set; }
    }
}
