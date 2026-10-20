using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Cached answer of one metadata-provider lookup (Deezer, MusicBrainz, Discogs…), keyed by provider
/// and request, so a re-run of library enrichment doesn't hit the network again. Only 200 (the
/// body) and 404 ("no such track") answers are stored; errors and rate limits never are.
/// </summary>
[Table("MetadataLookupCache")]
public class MetadataLookupCacheEntity
{
    [Required] public string Provider { get; set; } = string.Empty;
    [Required] public string CacheKey { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? Payload { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Where one stored metadata field of a track came from: <c>Source</c> is a provider name
/// ("deezer", "musicbrainz", "tags"…) or "user" for a manual edit, which enrichment never
/// overwrites. Shown as the inspector's "Sources" line.
/// </summary>
[Table("MetadataProvenance")]
public class MetadataProvenanceEntity
{
    [Required] public string TrackUniqueHash { get; set; } = string.Empty;
    [Required] public string Field { get; set; } = string.Empty;
    [Required] public string Source { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
