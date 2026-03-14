using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities
{
    public enum LibraryActionType
    {
        SmartSort = 0,
        ManualMove = 1,
        Rename = 2,
        Consolidate = 3
    }

    [Table("LibraryActionLogs")]
    public class LibraryActionLogEntity
    {
        [Key]
        public int Id { get; set; }

        public Guid BatchId { get; set; }

        public LibraryActionType ActionType { get; set; }

        [Required]
        public string SourcePath { get; set; } = string.Empty;

        [Required]
        public string DestinationPath { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; } = DateTime.Now;

        // Optional metadata
        public string? TrackArtist { get; set; }
        public string? TrackTitle { get; set; }
    }
}
