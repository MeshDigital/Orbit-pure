using System;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Services.Library;

namespace SLSKDONET.Models;

/// <summary>
/// Phase 23: DTO for serializing smart crate rules.
/// Extended in Epic #561 / #566 (GV5) with canonical <see cref="GenreKey"/> fields stored inside RulesJson.
/// </summary>
public class SmartCrateRules
{
    public string? Mood { get; set; }
    public string? SubGenre { get; set; }

    public GenreBucket? GenreBucket { get; set; }
    public string? GenreSubgenre { get; set; }
    public bool GenreIsUnclassified { get; set; }
    
    public double? MinBpm { get; set; }
    public double? MaxBpm { get; set; }
    
    public double? MinEnergy { get; set; }
    public double? MaxEnergy { get; set; }
    
    public bool OnlyInstrumental { get; set; }

    public GenreKey? GetGenreKey()
    {
        if (GenreIsUnclassified)
            return GenreKey.Unclassified;
        if (GenreBucket.HasValue)
            return new GenreKey(GenreBucket.Value, string.IsNullOrWhiteSpace(GenreSubgenre) ? null : GenreSubgenre.Trim());
        return null;
    }

    public void SetGenreKey(GenreKey? key)
    {
        if (key is null)
        {
            GenreBucket = null;
            GenreSubgenre = null;
            GenreIsUnclassified = false;
            return;
        }

        GenreBucket = key.Value.IsUnclassified ? null : key.Value.Bucket;
        GenreSubgenre = key.Value.IsUnclassified ? null : key.Value.Subgenre;
        GenreIsUnclassified = key.Value.IsUnclassified;
    }
}
