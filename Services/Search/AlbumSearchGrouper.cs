using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.Search;

/// <summary>
/// #427 (Album-mode search): groups ad-hoc search results by normalized (Artist, Album),
/// collapses duplicate peers/files per track to the best-ranked match, and preserves all
/// alternates so the user can expand a group and force a specific peer/file or queue the
/// whole album in one click.
/// </summary>
public static partial class AlbumSearchGrouper
{
    public const string SinglesFallbackAlbum = "Singles / Unsorted";

    [GeneratedRegex(@"^\s*\d{1,3}\s*[-._)]\s*", RegexOptions.Compiled)]
    private static partial Regex LeadingTrackNumberRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiWhitespaceRegex();

    public static IReadOnlyList<SearchAlbumGroupViewModel> Group(IEnumerable<AnalyzedSearchResultViewModel> results)
    {
        var byAlbum = new Dictionary<string, (string Artist, string Album, List<AnalyzedSearchResultViewModel> Items)>(StringComparer.Ordinal);

        foreach (var item in results)
        {
            var (artist, album) = ExtractArtistAndAlbum(item);
            var key = $"{NormalizeKey(artist)}||{NormalizeKey(album)}";
            if (!byAlbum.TryGetValue(key, out var bucket))
            {
                bucket = (artist, album, new List<AnalyzedSearchResultViewModel>());
                byAlbum[key] = bucket;
            }
            bucket.Items.Add(item);
        }

        var groups = new List<SearchAlbumGroupViewModel>(byAlbum.Count);
        foreach (var (_, bucket) in byAlbum)
        {
            var byTrack = new Dictionary<string, List<AnalyzedSearchResultViewModel>>(StringComparer.Ordinal);
            foreach (var item in bucket.Items)
            {
                var trackKey = NormalizeTrackTitle(item.RawResult.Title, item.RawResult.Filename);
                if (!byTrack.TryGetValue(trackKey, out var trackList))
                {
                    trackList = new List<AnalyzedSearchResultViewModel>();
                    byTrack[trackKey] = trackList;
                }
                trackList.Add(item);
            }

            var trackGroups = new List<SearchAlbumTrackGroupViewModel>(byTrack.Count);
            foreach (var (_, candidates) in byTrack)
            {
                var ranked = candidates
                    .OrderBy(c => c.IsFilteredOut ? 1 : 0)
                    .ThenByDescending(c => c.RawResult.CurrentRank)
                    .ThenByDescending(c => IsLosslessFormat(c.RawResult.FileFormat) ? 1 : 0)
                    .ThenByDescending(c => c.RawResult.Bitrate)
                    .ThenByDescending(c => c.RawResult.HasFreeUploadSlot ? 1 : 0)
                    .ToList();

                var best = ranked[0];
                var alternates = ranked.Count > 1 ? ranked.Skip(1).ToList() : (IReadOnlyList<AnalyzedSearchResultViewModel>)Array.Empty<AnalyzedSearchResultViewModel>();
                trackGroups.Add(new SearchAlbumTrackGroupViewModel(best, alternates));
            }

            var orderedTracks = trackGroups
                .OrderBy(t => t.Best.RawResult.Filename, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var peerCount = bucket.Items
                .Select(i => i.RawResult.Username)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            groups.Add(new SearchAlbumGroupViewModel(
                bucket.Album,
                bucket.Artist,
                orderedTracks,
                bucket.Items.Count,
                peerCount));
        }

        return groups
            .OrderByDescending(g => g.Tracks.Count > 1 ? 1 : 0)
            .ThenByDescending(g => g.BestRank)
            .ThenByDescending(g => g.Tracks.Count)
            .ThenBy(g => g.AlbumTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static (string Artist, string Album) ExtractArtistAndAlbum(AnalyzedSearchResultViewModel item)
    {
        var model = item.RawResult.Model;
        var artist = string.IsNullOrWhiteSpace(model.Artist) || string.Equals(model.Artist, "Unknown Artist", StringComparison.OrdinalIgnoreCase)
            ? ExtractFolderSegment(model.Filename, fromEnd: 2) ?? "Unknown Artist"
            : model.Artist.Trim();

        var album = !string.IsNullOrWhiteSpace(model.Album) && !string.Equals(model.Album, "Unknown Album", StringComparison.OrdinalIgnoreCase)
            ? model.Album.Trim()
            : ExtractFolderSegment(model.Filename, fromEnd: 1) ?? SinglesFallbackAlbum;

        return (artist, album);
    }

    internal static string NormalizeTrackTitle(string? title, string? filename)
    {
        var raw = !string.IsNullOrWhiteSpace(title) && !string.Equals(title, "Unknown Track", StringComparison.OrdinalIgnoreCase)
            ? title
            : System.IO.Path.GetFileNameWithoutExtension(filename ?? string.Empty);

        var stripped = LeadingTrackNumberRegex().Replace(raw ?? string.Empty, string.Empty);
        return NormalizeKey(stripped);
    }

    private static string? ExtractFolderSegment(string? path, int fromEnd)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        // parts[^1] is the filename; parts[^2] is parent folder (fromEnd=1); parts[^3] is grandparent (fromEnd=2)
        var idx = parts.Length - 1 - fromEnd;
        if (idx < 0 || idx >= parts.Length - 1) return null;
        var seg = parts[idx].Trim();
        return string.IsNullOrWhiteSpace(seg) ? null : seg;
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return MultiWhitespaceRegex().Replace(value.Trim().ToLowerInvariant(), " ");
    }

    private static bool IsLosslessFormat(string? format) =>
        format is not null && (
            format.Equals("FLAC", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("WAV", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("AIFF", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("AIF", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("ALAC", StringComparison.OrdinalIgnoreCase));
}
