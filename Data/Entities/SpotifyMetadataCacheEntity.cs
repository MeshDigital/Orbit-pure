using System;
using System.ComponentModel.DataAnnotations;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Cache for Spotify API responses to respect rate limits and reduce API calls.
/// </summary>
public class SpotifyMetadataCacheEntity
{
    [Key]
    public string SpotifyId { get; set; } = string.Empty; // "track:12345" or "search:artist:title"

    public string DataJson { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
