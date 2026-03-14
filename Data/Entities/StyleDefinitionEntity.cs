using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// A user-defined sonic style bucket (e.g., "Neurofunk", "Liquid").
/// Stores the mathematical centroid of the reference tracks.
/// </summary>
[Table("style_definitions")]
public class StyleDefinitionEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Name { get; set; } = string.Empty;

    public string ColorHex { get; set; } = "#888888";

    public string ParentGenre { get; set; } = string.Empty;

    /// <summary>
    /// JSON serialization of the centroid vector (List of floats).
    /// This represents the "average" sound of this style.
    /// </summary>
    public string CentroidJson { get; set; } = "[]";

    /// <summary>
    /// List of TrackUniqueHashes that the user dragged into this bucket.
    /// Stored as JSON string.
    /// </summary>
    public string ReferenceTrackHashesJson { get; set; } = "[]";
    
    [NotMapped]
    public List<string> ReferenceTrackHashes 
    {
        get 
        {
            if (string.IsNullOrEmpty(ReferenceTrackHashesJson)) return new List<string>();
            try 
            {
                return JsonSerializer.Deserialize<List<string>>(ReferenceTrackHashesJson) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }
        set => ReferenceTrackHashesJson = JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public List<float> Centroid
    {
        get
        {
            if (string.IsNullOrEmpty(CentroidJson)) return new List<float>();
            try
            {
                return JsonSerializer.Deserialize<List<float>>(CentroidJson) ?? new List<float>();
            }
            catch
            {
                return new List<float>();
            }
        }
        set => CentroidJson = JsonSerializer.Serialize(value);
    }
}
