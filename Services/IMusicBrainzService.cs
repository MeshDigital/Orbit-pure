using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

public class MusicBrainzCredits
{
    public List<string> Producers { get; set; } = new();
    public List<string> Mixers { get; set; } = new();
    public List<string> Engineers { get; set; } = new();
    public List<string> Labels { get; set; } = new();
    public string? RecordingId { get; set; }
    public string? ReleaseId { get; set; }
    public string? ReleaseTitle { get; set; }
    public string? Date { get; set; }
}

public interface IMusicBrainzService
{
    /// <summary>
    /// Resolves a MusicBrainz Recording ID (MBID) from an ISRC.
    /// </summary>
    Task<string?> ResolveMbidFromIsrcAsync(string isrc);

    /// <summary>
    /// Fetches deep credits (Producers, Mixers, etc.) for a given Recording MBID.
    /// </summary>
    Task<MusicBrainzCredits?> GetCreditsAsync(string mbid);

    /// <summary>
    /// High-level method to enrich a track with MusicBrainz metadata using its ISRC.
    /// </summary>
    Task<bool> EnrichTrackWithIsrcAsync(string trackUniqueHash, string isrc);
}
