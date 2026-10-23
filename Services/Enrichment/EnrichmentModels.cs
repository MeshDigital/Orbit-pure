using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.Enrichment;

/// <summary>The metadata a provider can fill. Several flags can be combined.</summary>
[Flags]
public enum MetadataField
{
    None = 0,
    Artwork = 1 << 0,
    Album = 1 << 1,
    Label = 1 << 2,
    ReleaseDate = 1 << 3,
    Isrc = 1 << 4,
    Genre = 1 << 5,
    Style = 1 << 6,
    TrackNumber = 1 << 7,
    Duration = 1 << 8,
    ExternalIds = 1 << 9,
    CatalogNumber = 1 << 10,
    /// <summary>Artist + title for a badly tagged file ("Unknown Artist", a file-name title).</summary>
    Identity = 1 << 11,
    /// <summary>Low-trust tempo/key hints. Never written over analysis-owned BPM/Key.</summary>
    BpmKeyHint = 1 << 12,
}

/// <summary>One track as the enrichment pipeline sees it (a LibraryEntries row snapshot).</summary>
public sealed class EnrichmentTrack
{
    public string Hash { get; init; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Album { get; set; }
    public string? FilePath { get; init; }
    public double? DurationSeconds { get; set; }
    public string? Label { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Genres { get; set; }
    public string? AlbumArtUrl { get; set; }
    public string? Isrc { get; set; }
    public string? MusicBrainzId { get; set; }
    public string? SpotifyTrackId { get; set; }
    public double? TagBpm { get; set; }

    /// <summary>True when the artist/title look like placeholders (no usable identity).</summary>
    public bool HasWeakIdentity =>
        IsPlaceholderArtist(Artist) || string.IsNullOrWhiteSpace(Title) || TitleLooksLikeFileName;

    public bool TitleLooksLikeFileName =>
        !string.IsNullOrWhiteSpace(FilePath) &&
        string.Equals(Title?.Trim(), System.IO.Path.GetFileNameWithoutExtension(FilePath), StringComparison.OrdinalIgnoreCase);

    public static bool IsPlaceholderArtist(string? artist) =>
        string.IsNullOrWhiteSpace(artist) ||
        string.Equals(artist.Trim(), "Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artist.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);
}

/// <summary>IDs found so far for a track, shared between providers within one pass.</summary>
public sealed class KnownIds
{
    public string? Isrc { get; set; }
    public string? MusicBrainzRecordingId { get; set; }
    public string? MusicBrainzReleaseId { get; set; }
    public string? DeezerTrackId { get; set; }
    public string? SpotifyTrackId { get; set; }
    public string? DiscogsReleaseId { get; set; }
}

/// <summary>What a provider found for one track. Every value is optional.</summary>
public sealed class MetadataCandidate
{
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string? Album { get; set; }
    public string? Label { get; set; }
    public string? CatalogNumber { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Isrc { get; set; }
    public List<string> Genres { get; } = new();
    public List<string> Styles { get; } = new();
    public int? TrackNumber { get; set; }
    public double? DurationSeconds { get; set; }
    /// <summary>Remote artwork URL (downloaded through ArtworkCacheService before it is stored).</summary>
    public string? ArtworkUrl { get; set; }
    /// <summary>Embedded artwork bytes (local tags).</summary>
    public byte[]? ArtworkBytes { get; set; }
    public string? MusicBrainzRecordingId { get; set; }
    public string? MusicBrainzReleaseId { get; set; }
    public string? DeezerTrackId { get; set; }
    public string? SpotifyTrackId { get; set; }
    public double? BpmHint { get; set; }
    public string? KeyHint { get; set; }

    /// <summary>0..1: how sure the provider is that this is the right recording.</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>The fields this candidate actually carries a value for.</summary>
    public MetadataField Supplied
    {
        get
        {
            var f = MetadataField.None;
            if (!string.IsNullOrWhiteSpace(ArtworkUrl) || ArtworkBytes is { Length: > 0 }) f |= MetadataField.Artwork;
            if (!string.IsNullOrWhiteSpace(Album)) f |= MetadataField.Album;
            if (!string.IsNullOrWhiteSpace(Label)) f |= MetadataField.Label;
            if (ReleaseDate.HasValue) f |= MetadataField.ReleaseDate;
            if (!string.IsNullOrWhiteSpace(Isrc)) f |= MetadataField.Isrc;
            if (Genres.Count > 0) f |= MetadataField.Genre;
            if (Styles.Count > 0) f |= MetadataField.Style;
            if (TrackNumber is > 0) f |= MetadataField.TrackNumber;
            if (DurationSeconds is > 0) f |= MetadataField.Duration;
            if (!string.IsNullOrWhiteSpace(CatalogNumber)) f |= MetadataField.CatalogNumber;
            if (!string.IsNullOrWhiteSpace(MusicBrainzRecordingId) || !string.IsNullOrWhiteSpace(DeezerTrackId) ||
                !string.IsNullOrWhiteSpace(SpotifyTrackId)) f |= MetadataField.ExternalIds;
            if (!string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Title)) f |= MetadataField.Identity;
            if (BpmHint is > 0 || !string.IsNullOrWhiteSpace(KeyHint)) f |= MetadataField.BpmKeyHint;
            return f;
        }
    }
}

public enum LookupStatus
{
    /// <summary>A confident match.</summary>
    Matched,
    /// <summary>The provider answered but has no (confident) match.</summary>
    NoMatch,
    /// <summary>429 or a quota error: try this track again later.</summary>
    RateLimited,
    /// <summary>The provider can't run (no key, no tool, disabled after 403, circuit open).</summary>
    Unavailable,
    /// <summary>Network error, 5xx, malformed payload.</summary>
    Failed,
}

/// <summary>A provider's answer for one track.</summary>
public sealed record ProviderLookup(LookupStatus Status, MetadataCandidate? Candidate = null, string? Error = null, TimeSpan? RetryAfter = null)
{
    public static ProviderLookup NoMatch() => new(LookupStatus.NoMatch);
    public static ProviderLookup Match(MetadataCandidate c) => new(LookupStatus.Matched, c);
    public static ProviderLookup Fail(string error) => new(LookupStatus.Failed, Error: error);
    public static ProviderLookup Unavailable(string reason) => new(LookupStatus.Unavailable, Error: reason);
    public static ProviderLookup Limited(TimeSpan? retryAfter, string? error = null) => new(LookupStatus.RateLimited, Error: error, RetryAfter: retryAfter);
}

/// <summary>Why a provider can or can't be used right now (shown in Settings and on the card).</summary>
public enum ProviderAvailability
{
    Ready,
    Disabled,
    NeedsKey,
    NeedsTool,
    NotConnected,
    /// <summary>Repeated 403s this session; switched off until restart.</summary>
    Blocked,
}

/// <summary>
/// Request pacing for one provider: at most <see cref="PermitsPerWindow"/> requests per
/// <see cref="Window"/>, never closer together than <see cref="MinSpacing"/> (+ up to
/// <see cref="Jitter"/> random extra), and at most <see cref="MaxConcurrent"/> in flight.
/// </summary>
public sealed record RatePolicy(int PermitsPerWindow, TimeSpan Window, TimeSpan MinSpacing, TimeSpan Jitter, int MaxConcurrent = 1)
{
    /// <summary>No network: local tag reads.</summary>
    public static RatePolicy Unlimited { get; } = new(int.MaxValue, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero, 4);

    /// <summary>Requests per second this policy allows at best (for estimates).</summary>
    public double MaxPerSecond
    {
        get
        {
            var bySpacing = MinSpacing > TimeSpan.Zero ? 1.0 / (MinSpacing + Jitter / 2).TotalSeconds : double.MaxValue;
            var byWindow = PermitsPerWindow == int.MaxValue ? double.MaxValue : PermitsPerWindow / Window.TotalSeconds;
            return Math.Min(bySpacing, byWindow);
        }
    }
}

/// <summary>Live per-provider counters for the Enrichment card.</summary>
public sealed record ProviderStatusSnapshot(
    string Name,
    string DisplayName,
    ProviderAvailability Availability,
    bool IsCircuitOpen,
    DateTime? OpenUntilUtc,
    int Requests,
    int Matched,
    int RateLimitPauses,
    string? LastError);

/// <summary>Progress of the enrichment queue (also published as an event).</summary>
public sealed record EnrichmentProgress(
    bool IsRunning,
    bool IsPaused,
    /// <summary>The analysis queue has work, so enrichment runs one track at a time.</summary>
    bool IsSlowedForAnalysis,
    int Total,
    int Done,
    int Enriched,
    int Pending,
    IReadOnlyList<ProviderStatusSnapshot> Providers)
{
    public static EnrichmentProgress Idle { get; } = new(false, false, false, 0, 0, 0, 0, Array.Empty<ProviderStatusSnapshot>());
}

/// <summary>Published (throttled) while the orchestrator works.</summary>
public sealed record EnrichmentProgressEvent(EnrichmentProgress Progress);

/// <summary>A batch of tracks whose stored metadata changed; the Library refreshes just those rows.</summary>
public sealed record TracksEnrichedEvent(IReadOnlyList<string> TrackHashes);
