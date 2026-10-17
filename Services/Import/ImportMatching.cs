using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Import;

/// <summary>
/// Identity helpers shared by the import preview and <see cref="DownloadManager.QueueProject"/>.
/// The preview tells the user "Owned" / "In playlist" per row; the commit then links or skips
/// those same rows. Both sides must use the exact same rules, or the preview promises one thing
/// and the commit does another.
/// </summary>
public static class ImportIdentity
{
    /// <summary>Lower-case letters and digits only, "artist|title". Empty when either part is empty.</summary>
    public static string LooseKey(string? artist, string? title)
    {
        var a = Normalize(artist);
        var t = Normalize(title);
        return a.Length == 0 || t.Length == 0 ? string.Empty : $"{a}|{t}";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}

/// <summary>
/// One-pass index over the library for "do I already own this file?". Lookup order: exact hash,
/// Spotify track id, loose artist|title key, then a fuzzy artist+title score of at least
/// <see cref="FuzzyOwnedThreshold"/>.
/// </summary>
public sealed class LibraryMatchIndex
{
    /// <summary>At or above this fuzzy score a library entry counts as the same recording.</summary>
    public const double FuzzyOwnedThreshold = 0.88;

    /// <summary>Between this and <see cref="FuzzyOwnedThreshold"/> a row is flagged "similar" for review.</summary>
    public const double FuzzySimilarThreshold = 0.75;

    private readonly Dictionary<string, LibraryEntry> _byHash;
    private readonly Dictionary<string, LibraryEntry> _bySpotifyId;
    private readonly Dictionary<string, LibraryEntry> _byLooseKey;
    private readonly List<(LibraryEntry Entry, string Key)> _fuzzy;

    public static LibraryMatchIndex Empty { get; } = new(Array.Empty<LibraryEntry>());

    public LibraryMatchIndex(IEnumerable<LibraryEntry> entries)
    {
        // Newest entry wins when several rows share a key (re-downloads, upgrades).
        var list = entries.OrderByDescending(e => e.AddedAt).ToList();

        _byHash = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        _bySpotifyId = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        _byLooseKey = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
        _fuzzy = new List<(LibraryEntry, string)>(list.Count);

        foreach (var e in list)
        {
            if (!string.IsNullOrWhiteSpace(e.UniqueHash)) _byHash.TryAdd(e.UniqueHash, e);
            if (!string.IsNullOrWhiteSpace(e.SpotifyTrackId)) _bySpotifyId.TryAdd(e.SpotifyTrackId!, e);

            var loose = ImportIdentity.LooseKey(e.Artist, e.Title);
            if (loose.Length > 0) _byLooseKey.TryAdd(loose, e);

            if (!string.IsNullOrWhiteSpace(e.Artist) && !string.IsNullOrWhiteSpace(e.Title))
                _fuzzy.Add((e, StringDistanceUtils.Normalize($"{e.Artist} {e.Title}")));
        }
    }

    public int Count => _fuzzy.Count;

    /// <summary>The owned library entry for this track, or null.</summary>
    public LibraryEntry? Resolve(string? artist, string? title, string? spotifyTrackId, string? hash)
        => Match(artist, title, spotifyTrackId, hash).Owned;

    /// <summary>
    /// Full lookup: <c>Owned</c> when the track is in the library, otherwise the best fuzzy
    /// candidate in the "similar" band (for a review hint), otherwise nothing.
    /// </summary>
    public (LibraryEntry? Owned, LibraryEntry? Similar, double Score) Match(
        string? artist, string? title, string? spotifyTrackId, string? hash)
    {
        if (!string.IsNullOrWhiteSpace(hash) && _byHash.TryGetValue(hash, out var byHash))
            return (byHash, null, 1.0);

        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            return (null, null, 0);

        if (!string.IsNullOrWhiteSpace(spotifyTrackId) && _bySpotifyId.TryGetValue(spotifyTrackId, out var bySpotify))
            return (bySpotify, null, 1.0);

        var loose = ImportIdentity.LooseKey(artist, title);
        if (loose.Length > 0 && _byLooseKey.TryGetValue(loose, out var byLoose))
            return (byLoose, null, 1.0);

        if (_fuzzy.Count == 0) return (null, null, 0);

        var inputKey = StringDistanceUtils.Normalize($"{artist} {title}");
        LibraryEntry? best = null;
        double bestScore = 0;
        foreach (var (entry, key) in _fuzzy)
        {
            var score = StringDistanceUtils.GetNormalizedMatchScore(inputKey, key);
            if (score > bestScore)
            {
                bestScore = score;
                best = entry;
            }
        }

        if (best == null) return (null, null, 0);
        if (bestScore >= FuzzyOwnedThreshold) return (best, null, bestScore);
        if (bestScore >= FuzzySimilarThreshold) return (null, best, bestScore);
        return (null, null, bestScore);
    }
}

/// <summary>
/// Index over the rows already in one playlist. A row can be stored under the library file's
/// own hash (QueueProject links owned tracks that way), so matching by the incoming track's hash
/// alone missed it and every re-import added the same song again. Lookup order: hash, the
/// resolved library hash, Spotify id, loose artist|title key.
/// </summary>
public sealed class PlaylistMembershipIndex
{
    private readonly Dictionary<string, PlaylistTrack> _byHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaylistTrack> _bySpotifyId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaylistTrack> _byLooseKey = new(StringComparer.Ordinal);

    public static PlaylistMembershipIndex Empty { get; } = new(Array.Empty<PlaylistTrack>());

    public PlaylistMembershipIndex(IEnumerable<PlaylistTrack> rows)
    {
        foreach (var r in rows)
        {
            Count++;
            if (!string.IsNullOrWhiteSpace(r.TrackUniqueHash)) _byHash.TryAdd(r.TrackUniqueHash, r);
            if (!string.IsNullOrWhiteSpace(r.SpotifyTrackId)) _bySpotifyId.TryAdd(r.SpotifyTrackId!, r);
            var loose = ImportIdentity.LooseKey(r.Artist, r.Title);
            if (loose.Length > 0) _byLooseKey.TryAdd(loose, r);
        }
    }

    public int Count { get; }

    public PlaylistTrack? Find(string? hash, string? libraryHash, string? spotifyTrackId, string? artist, string? title)
    {
        if (!string.IsNullOrWhiteSpace(hash) && _byHash.TryGetValue(hash, out var a)) return a;
        if (!string.IsNullOrWhiteSpace(libraryHash) && _byHash.TryGetValue(libraryHash, out var b)) return b;
        if (!string.IsNullOrWhiteSpace(spotifyTrackId) && _bySpotifyId.TryGetValue(spotifyTrackId, out var c)) return c;
        var loose = ImportIdentity.LooseKey(artist, title);
        if (loose.Length > 0 && _byLooseKey.TryGetValue(loose, out var d)) return d;
        return null;
    }

    /// <summary>True when a matched row is failed/on hold — re-importing should retry it.</summary>
    public static bool NeedsRetry(PlaylistTrack row) =>
        row.Status == TrackStatus.Failed || row.Status == TrackStatus.OnHold;
}

/// <summary>Turns a provider's <see cref="SearchQuery"/> into the <see cref="Track"/> the commit persists.</summary>
public static class ImportTrackMapper
{
    public static Track ToTrack(SearchQuery query) => new()
    {
        Title = query.Title,
        Artist = query.Artist,
        Album = query.Album,
        Length = query.Length,
        OriginalArtist = query.OriginalArtist,
        OriginalTitle = query.OriginalTitle,
        SpotifyTrackId = query.SpotifyTrackId,
        SpotifyAlbumId = query.SpotifyAlbumId,
        SpotifyArtistId = query.SpotifyArtistId,
        SpotifyPlaylistId = query.SpotifyPlaylistId,
        SpotifyUri = query.SpotifyUri,
        AlbumArtUrl = query.AlbumArtUrl,
        ArtistImageUrl = query.ArtistImageUrl,
        Genres = query.Genres,
        Popularity = query.Popularity,
        CanonicalDuration = query.CanonicalDuration,
        ReleaseDate = query.ReleaseDate
    };
}

/// <summary>What <see cref="DownloadManager.QueueProject"/> actually did with an import.</summary>
public sealed record ProjectQueueResult(
    int Added,
    int LinkedFromLibrary,
    int Retried,
    int ReconciledExisting,
    int AlreadyInPlaylist,
    int SkippedInvalidOrRepeated)
{
    public static ProjectQueueResult None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>New rows that will be searched and downloaded (added minus linked) plus retries.</summary>
    public int QueuedForDownload => Added - LinkedFromLibrary + Retried;
}
