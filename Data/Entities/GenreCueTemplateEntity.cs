using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Defines a genre-specific cue point template.
/// Different genres have different track structures and require different cue placements.
/// </summary>
[Table("GenreCueTemplates")]
public class GenreCueTemplateEntity
{
    [Key]
    public int Id { get; set; }
    
    /// <summary>
    /// Genre name (e.g., "DnB", "House", "Techno", "Dubstep").
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string GenreName { get; set; } = string.Empty;
    
    /// <summary>
    /// Display name for the template.
    /// </summary>
    [MaxLength(100)]
    public string? DisplayName { get; set; }
    
    /// <summary>
    /// Whether this is a built-in template (cannot be deleted).
    /// </summary>
    public bool IsBuiltIn { get; set; }
    
    // ============================================
    // Cue Point Definitions (up to 8 cues)
    // ============================================
    
    /// <summary>
    /// Target phrase type for Cue 1 (e.g., "Drop", "Intro").
    /// </summary>
    public PhraseType Cue1Target { get; set; }
    
    /// <summary>
    /// Bar offset for Cue 1 relative to target phrase.
    /// Negative = before phrase, Positive = after phrase start.
    /// </summary>
    public int Cue1OffsetBars { get; set; }
    
    /// <summary>
    /// Color for Cue 1 (hex, e.g., "#FF0000").
    /// </summary>
    [MaxLength(7)]
    public string Cue1Color { get; set; } = "#FF0000";
    
    /// <summary>
    /// Label for Cue 1 (e.g., "Drop", "Build").
    /// </summary>
    [MaxLength(20)]
    public string? Cue1Label { get; set; }
    
    // Cue 2
    public PhraseType Cue2Target { get; set; }
    public int Cue2OffsetBars { get; set; }
    [MaxLength(7)]
    public string Cue2Color { get; set; } = "#00FF00";
    [MaxLength(20)]
    public string? Cue2Label { get; set; }
    
    // Cue 3
    public PhraseType Cue3Target { get; set; }
    public int Cue3OffsetBars { get; set; }
    [MaxLength(7)]
    public string Cue3Color { get; set; } = "#0000FF";
    [MaxLength(20)]
    public string? Cue3Label { get; set; }
    
    // Cue 4
    public PhraseType Cue4Target { get; set; }
    public int Cue4OffsetBars { get; set; }
    [MaxLength(7)]
    public string Cue4Color { get; set; } = "#FFFF00";
    [MaxLength(20)]
    public string? Cue4Label { get; set; }
    
    // Cue 5-8 (optional, for power users)
    public PhraseType? Cue5Target { get; set; }
    public int? Cue5OffsetBars { get; set; }
    [MaxLength(7)]
    public string? Cue5Color { get; set; }
    [MaxLength(20)]
    public string? Cue5Label { get; set; }
    
    public PhraseType? Cue6Target { get; set; }
    public int? Cue6OffsetBars { get; set; }
    [MaxLength(7)]
    public string? Cue6Color { get; set; }
    [MaxLength(20)]
    public string? Cue6Label { get; set; }
    
    public PhraseType? Cue7Target { get; set; }
    public int? Cue7OffsetBars { get; set; }
    [MaxLength(7)]
    public string? Cue7Color { get; set; }
    [MaxLength(20)]
    public string? Cue7Label { get; set; }
    
    public PhraseType? Cue8Target { get; set; }
    public int? Cue8OffsetBars { get; set; }
    [MaxLength(7)]
    public string? Cue8Color { get; set; }
    [MaxLength(20)]
    public string? Cue8Label { get; set; }
}
