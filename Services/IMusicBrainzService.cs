using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

public class MusicBrainzCredits
{
    // Recording identity
    public string? RecordingId { get; set; }
    public string? RecordingTitle { get; set; }
    public string? ArtistName { get; set; }
    public int? DurationMs { get; set; }
    public string? ISRC { get; set; }

    // Release metadata
    public string? ReleaseId { get; set; }
    public string? ReleaseTitle { get; set; }
    public string? Date { get; set; }
    public List<string> Labels { get; set; } = new();

    // Credits
    public List<string> Producers { get; set; } = new();
    public List<string> Mixers { get; set; } = new();
    public List<string> Engineers { get; set; } = new();
    public List<string> Composers { get; set; } = new();
    public List<string> Lyricists { get; set; } = new();
    public List<string> Songwriters { get; set; } = new();

    // Genre / mood tags from MusicBrainz community
    public List<string> Tags { get; set; } = new();
}

public interface IMusicBrainzService
{
    /// <summary>
    /// Resolves a MusicBrainz Recording ID (MBID) from an ISRC.
    /// </summary>
    Task<string?> ResolveMbidFromIsrcAsync(string isrc);

    /// <summary>
    /// Fetches deep credits (Producers, Mixers, Composers, etc.) and tags for a given Recording MBID.
    /// </summary>
    Task<MusicBrainzCredits?> GetCreditsAsync(string mbid);

    /// <summary>
    /// Searches MusicBrainz for a recording by artist name and track title.
    /// Returns credits metadata if a confident match is found, otherwise null.
    /// </summary>
    Task<MusicBrainzCredits?> SearchByArtistTitleAsync(string artist, string title);

    /// <summary>
    /// High-level method to enrich a track with MusicBrainz metadata using its ISRC.
    /// </summary>
    Task<bool> EnrichTrackWithIsrcAsync(string trackUniqueHash, string isrc);

    /// <summary>
    /// High-level method to enrich a track with MusicBrainz metadata by searching for artist + title.
    /// Useful when the track has no ISRC. Falls back gracefully if no confident match is found.
    /// </summary>
    Task<bool> EnrichTrackByNameAsync(string trackUniqueHash, string artist, string title);
}
