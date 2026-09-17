namespace SLSKDONET.Utils;

/// <summary>
/// The single canonical "artist-title" identity hash used to deduplicate a track before it has
/// been downloaded (no audio content to fingerprint yet). Confirmed, by sampling real downloaded
/// tracks, to be the dominant format already used across almost this entire library (e.g.
/// "blainestranger-dragon", "subfocus-miracle-vipmix") — it originated as a computed property on
/// <see cref="SLSKDONET.Models.Track"/> and needs to be reproducible from anywhere a track's
/// artist/title is known but no <c>Track</c> instance exists yet (import providers building
/// <c>SearchQuery</c> objects, for instance).
///
/// This exists because that formula had silently drifted into at least two other, incompatible
/// variants in different corners of the codebase — <c>SpotifyScraperInputSource</c> used
/// "artist|title" (pipe, spaces preserved), and a fix to <c>SpotifyInputSource</c> briefly
/// introduced a third variant — each of which broke sync-merge deduplication for any track whose
/// hash had been established under a different variant, causing the same song to be silently
/// re-added as a "new" duplicate on every sync. One shared implementation is the only way to keep
/// that from happening again.
/// </summary>
public static class TrackHashUtil
{
    public static string Compute(string? artist, string? title) =>
        $"{artist?.ToLower().Replace(" ", "")}-{title?.ToLower().Replace(" ", "")}".TrimStart('-').TrimEnd('-');
}
