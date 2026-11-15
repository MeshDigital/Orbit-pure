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
            .Select(t => t.Genres)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(playlistGenres) ? null : new TrackGenreContext(playlistGenres, row?.Source);
    }
}
