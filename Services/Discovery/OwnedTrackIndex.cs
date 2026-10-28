using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SLSKDONET.Services.Discovery;

/// <summary>
/// Answers "do I already have this?" for Discover suggestions, across library files and every
/// playlist row (downloaded, queued or missing — a queued track is already on its way).
///
/// Matching is by title first, then artist overlap, because the two sides credit artists
/// differently: the library says "Sub Focus &amp; Wilkinson" or "Camo &amp; Krooked", Beatport says
/// "Sub Focus, Wilkinson". Artists count as matching when either normalized name contains the
/// other, so both joint-credit and single-credit forms match. Titles are compared with
/// the default-version tag removed in both spellings the library uses ("Tesla (Original Mix)"
/// and "Tesla - Original Mix"), while real versions stay distinct ("Tesla (VIP)" is a different track).
/// </summary>
public sealed class OwnedTrackIndex
{
    private const int MinArtistLength = 3;
    private static readonly HashSet<string> UnknownArtists = new(StringComparer.Ordinal)
    {
        "", "unknown", "unknownartist", "variousartists", "va",
    };
    private static readonly Regex AmpersandSplit = new(@"\s*(?:&|\band\b|\+)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // normalized title → normalized artist strings of every owned track with that title
    private readonly Dictionary<string, List<string>> _byTitle = new(StringComparer.Ordinal);

    public int Count { get; private set; }

    public void Add(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        var normalizedArtist = PlaylistDiscoveryService.NormalizeArtist(artist);
        // Files tagged only by filename: "Unknown Artist" / "Metrik - Gravity".
        if (UnknownArtists.Contains(normalizedArtist) && SplitFilenameTitle(title) is { } parsed)
        {
            Add(parsed.Artist, parsed.Title);
            return;
        }

        var key = PlaylistDiscoveryService.NormalizeTitle(title);
        if (key.Length == 0) return;
        if (!_byTitle.TryGetValue(key, out var artists))
            _byTitle[key] = artists = new List<string>(1);
        artists.Add(normalizedArtist);
        Count++;
    }

    /// <summary>Compares the full title including the mix name — owning "Gravity" doesn't mean owning "Gravity (VIP)".</summary>
    public bool Contains(DiscoveryCandidate c) => Contains(c.Artist, c.SearchTitle);

    public bool Contains(string? artist, string? title)
    {
        var key = PlaylistDiscoveryService.NormalizeTitle(title);
        if (key.Length == 0 || !_byTitle.TryGetValue(key, out var ownedArtists)) return false;

        var parts = ArtistParts(artist).ToList();
        if (parts.Count == 0) return false;
        return ownedArtists.Any(owned =>
            UnknownArtists.Contains(owned)
            || parts.Any(p => owned.Contains(p, StringComparison.Ordinal) || (owned.Length >= MinArtistLength && p.Contains(owned, StringComparison.Ordinal))));
    }

    /// <summary>Every credited name, also split on "&amp;"/"and", normalized; too-short fragments dropped.</summary>
    private static IEnumerable<string> ArtistParts(string? artist)
    {
        foreach (var credit in PlaylistDiscoveryService.SplitArtists(artist))
        {
            var whole = PlaylistDiscoveryService.NormalizeArtist(credit);
            if (whole.Length >= MinArtistLength) yield return whole;
            foreach (var piece in AmpersandSplit.Split(credit))
            {
                var n = PlaylistDiscoveryService.NormalizeArtist(piece);
                if (n.Length >= MinArtistLength && n != whole) yield return n;
            }
        }
    }

    private static (string Artist, string Title)? SplitFilenameTitle(string title)
    {
        var i = title.IndexOf(" - ", StringComparison.Ordinal);
        if (i <= 0 || i + 3 >= title.Length) return null;
        return (title[..i].Trim(), title[(i + 3)..].Trim());
    }
}
