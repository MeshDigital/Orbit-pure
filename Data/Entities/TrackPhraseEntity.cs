using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Represents a detected phrase/segment within a track.
/// Used for genre-aware cue point generation.
/// </summary>
[Table("TrackPhrases")]
public class TrackPhraseEntity
{
    [Key]
    public int Id { get; set; }
    
    /// <summary>
    /// Foreign key to the analyzed track.
    /// </summary>
    [Required]
    public string TrackUniqueHash { get; set; } = string.Empty;
    
    /// <summary>
    /// Type of phrase (Intro, Build, Drop, etc.)
    /// </summary>
    public PhraseType Type { get; set; }
    
    /// <summary>
    /// Start timestamp in seconds.
    /// </summary>
    public float StartTimeSeconds { get; set; }
    
    /// <summary>
    /// End timestamp in seconds.
    /// </summary>
    public float EndTimeSeconds { get; set; }
    
    /// <summary>
    /// Duration in seconds (computed).
    /// </summary>
    [NotMapped]
    public float DurationSeconds => EndTimeSeconds - StartTimeSeconds;
    
    /// <summary>
    /// Average energy level of this phrase (0.0 - 1.0).
    /// Used for identifying drops vs breakdowns.
    /// </summary>
    public float EnergyLevel { get; set; }
    
    /// <summary>
    /// Confidence of the phrase detection (0.0 - 1.0).
    /// </summary>
    public float Confidence { get; set; }
    
    /// <summary>
    /// Order index within the track (0 = first phrase).
    /// </summary>
    public int OrderIndex { get; set; }
    
    /// <summary>
    /// Optional label (e.g., "Main Drop", "Second Verse").
    /// </summary>
    public string? Label { get; set; }
}

/// <summary>
/// Types of musical phrases that can be detected.
/// </summary>
public enum PhraseType
{
    Unknown = 0,
    Intro = 1,
    Verse = 2,
    Chorus = 3,
    Build = 4,
    Drop = 5,
    Breakdown = 6,
    Bridge = 7,
    Outro = 8,
    
    // Electronic-specific
    Riser = 10,
    Filter = 11,
    Ambient = 12
}
