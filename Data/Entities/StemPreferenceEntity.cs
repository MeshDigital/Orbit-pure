using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

public class StemPreferenceEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string TrackUniqueHash { get; set; } = string.Empty;

    // JSON serialized lists for flexibility (Muted/Solo StemTypes)
    public string AlwaysMutedJson { get; set; } = "[]";
    public string AlwaysSoloJson { get; set; } = "[]";

    public DateTime LastModified { get; set; } = DateTime.Now;

    // Navigation property if needed
    [ForeignKey(nameof(TrackUniqueHash))]
    public LibraryEntryEntity? LibraryEntry { get; set; }
}
