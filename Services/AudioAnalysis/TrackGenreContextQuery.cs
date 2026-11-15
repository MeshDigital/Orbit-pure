using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Services.Enrichment;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Loads a track's library genre and the source that wrote it, for <see cref="TrackGenreResolver"/> (GA8 #253).
/// One query: <c>LibraryEntries.Genres</c> with the <c>MetadataProvenance</c> "genre" row as a sub-select; a
/// track with no library entry falls back to its playlist row's genre. Nothing is cached, so a genre the user
/// edits is used by the very next cue generation.
/// </summary>
public static class TrackGenreContextQuery
{
    public static async Task<TrackGenreContext?> LoadAsync(AppDbContext db, string trackHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackHash)) return null;
        const string genreField = MetadataMerger.Fields.Genre;

        var row = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.UniqueHash == trackHash)
            .Select(e => new
            {
                e.Genres,
                Source = db.MetadataProvenance
                    .Where(p => p.TrackUniqueHash == trackHash && p.Field == genreField)
                    .Select(p => p.Source)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (row is not null && !string.IsNullOrWhiteSpace(row.Genres))
            return new TrackGenreContext(row.Genres, row.Source);

        var playlistGenres = await db.PlaylistTracks.AsNoTracking()
            .Where(t => t.TrackUniqueHash == trackHash && t.Genres != null && t.Genres != "")
            .OrderBy(t => t.Id) // deterministic, and the same row LoadManyAsync picks
            .Select(t => t.Genres)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(playlistGenres) ? null : new TrackGenreContext(playlistGenres, row?.Source);
    }

    /// <summary>
    /// <see cref="LoadAsync"/> for many tracks in three queries (library genres, provenance, playlist fallback),
    /// with the same rules. Hashes without a genre are absent from the result.
    /// </summary>
    public static async Task<Dictionary<string, TrackGenreContext>> LoadManyAsync(
        AppDbContext db, IReadOnlyCollection<string> trackHashes, CancellationToken ct = default)
    {
        var result = new Dictionary<string, TrackGenreContext>(System.StringComparer.Ordinal);
        if (trackHashes.Count == 0) return result;
        const string genreField = MetadataMerger.Fields.Genre;
        var hashes = trackHashes.Distinct().ToList();

        var library = await db.LibraryEntries.AsNoTracking()
            .Where(e => hashes.Contains(e.UniqueHash))
            .Select(e => new { e.UniqueHash, e.Genres })
            .ToListAsync(ct).ConfigureAwait(false);
        var sources = await db.MetadataProvenance.AsNoTracking()
            .Where(p => p.Field == genreField && hashes.Contains(p.TrackUniqueHash))
            .Select(p => new { p.TrackUniqueHash, p.Source })
            .ToListAsync(ct).ConfigureAwait(false);
        var sourceByHash = sources.GroupBy(s => s.TrackUniqueHash).ToDictionary(g => g.Key, g => g.First().Source);
        var inLibrary = library.Select(l => l.UniqueHash).ToHashSet();

        foreach (var entry in library)
        {
            if (string.IsNullOrWhiteSpace(entry.Genres) || result.ContainsKey(entry.UniqueHash)) continue;
            result[entry.UniqueHash] = new TrackGenreContext(entry.Genres, sourceByHash.GetValueOrDefault(entry.UniqueHash));
        }

        var missing = hashes.Where(h => !result.ContainsKey(h)).ToList();
        if (missing.Count > 0)
        {
            var playlist = await db.PlaylistTracks.AsNoTracking()
                .Where(t => missing.Contains(t.TrackUniqueHash) && t.Genres != null && t.Genres != "")
                .OrderBy(t => t.Id)
                .Select(t => new { t.TrackUniqueHash, t.Genres })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in playlist)
            {
                if (string.IsNullOrWhiteSpace(row.Genres) || result.ContainsKey(row.TrackUniqueHash)) continue;
                // As LoadAsync: the provenance row only counts when the track has a library entry.
                result[row.TrackUniqueHash] = new TrackGenreContext(row.Genres,
                    inLibrary.Contains(row.TrackUniqueHash) ? sourceByHash.GetValueOrDefault(row.TrackUniqueHash) : null);
            }
        }

        return result;
    }
}
