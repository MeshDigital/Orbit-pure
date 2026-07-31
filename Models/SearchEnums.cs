namespace SLSKDONET.Models;

public enum SearchInputMode
{
    // AdHoc search queries Soulseek directly (default)
    AdHoc,
    // Spotify processes a URL/URI
    SpotifyLink,
    // CSV processes a local file path
    CsvFile
}

public enum RankingPreset
{
    // Prioritizes a balance of Bitrate, File Size, and Share Count
    Balanced,
    // Prioritizes FLAC and 320kbps MP3s (highest quality)
    HighQuality,
    // Prioritizes fastest downloads (low size, high share count, lowest duration)
    FastestDownload
}

/// <summary>
/// Maps to the Soulseek protocol's own SearchScope. Wishlist is the protocol's purpose-built
/// low-priority scope for unattended background searches (server-side gentler rate limiting);
/// only use it where nobody is actively waiting on the result.
/// </summary>
public enum SearchScopeKind
{
    Network,
    Wishlist
}
