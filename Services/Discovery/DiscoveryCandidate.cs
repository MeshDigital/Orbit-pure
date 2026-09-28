using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.Discovery;

/// <summary>Where a playlist suggestion was found.</summary>
[Flags]
public enum DiscoverySource
{
    None = 0,
    BeatportArtist = 1,   // a new/other release by an artist already in the playlist
    BeatportChart = 2,    // Beatport top 100 of the playlist's genre
    DeezerRelated = 4,    // top track of an artist Deezer lists as related to a playlist artist
    DeezerArtist = 8,     // top track of an artist already in the playlist
    BeatportMatch = 16,   // not suggested by Beatport, but found there by lookup (adds BPM/key/genre and a buy link)

    BeatportSuggested = BeatportArtist | BeatportChart,
    DeezerSuggested = DeezerArtist | DeezerRelated,
}

/// <summary>
/// One external track that could be added to a playlist. Built from Beatport and/or Deezer
/// catalog data; fields a source doesn't know stay null.
/// </summary>
public sealed class DiscoveryCandidate
{
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>Beatport mix name ("Original Mix", "VIP", "Extended Mix"…); null when unknown.</summary>
    public string? MixName { get; set; }
    public string? Album { get; set; }
    public string? Label { get; set; }
    public double? Bpm { get; set; }

    /// <summary>Camelot notation ("8A"), or null when unknown.</summary>
    public string? CamelotKey { get; set; }
    public string? Genre { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public double? DurationSeconds { get; set; }
    public string? ImageUrl { get; set; }
    public string? PreviewUrl { get; set; }
    public string? BeatportUrl { get; set; }
    public string? DeezerUrl { get; set; }
    public string? Price { get; set; }

    public DiscoverySource Sources { get; set; }

    /// <summary>
    /// Source-specific strength in 0..1: chart position, how closely an artist is related, or
    /// how recent an artist release is. Merged candidates keep the highest.
    /// </summary>
    public double SourceStrength { get; set; }

    /// <summary>The playlist artist this was found through ("Related to Metrik").</summary>
    public string? ViaArtist { get; set; }

    /// <summary>How many separate suggestion paths led here (e.g. related to two playlist artists).</summary>
    public int HitCount { get; set; } = 1;

    /// <summary>Both Beatport and Deezer recommended this independently.</summary>
    public bool IsCrossSource =>
        (Sources & DiscoverySource.BeatportSuggested) != 0 && (Sources & DiscoverySource.DeezerSuggested) != 0;

    /// <summary>Position in the Beatport genre top 100, when found there.</summary>
    public int? ChartPosition { get; set; }

    /// <summary>Filled by ranking.</summary>
    public double Score { get; set; }
    [System.Text.Json.Serialization.JsonObjectCreationHandling(System.Text.Json.Serialization.JsonObjectCreationHandling.Populate)]
    public List<string> Reasons { get; } = new(); // Populate: restored from the Discover disk cache

    /// <summary>The title as a Soulseek search should see it: includes the mix name unless it is the default.</summary>
    public string SearchTitle =>
        string.IsNullOrWhiteSpace(MixName)
        || MixName.Equals("Original Mix", StringComparison.OrdinalIgnoreCase)
        || Title.Contains(MixName, StringComparison.OrdinalIgnoreCase)
            ? Title
            : $"{Title} ({MixName})";
}
