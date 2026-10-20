using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Library;

/// <summary>
/// Lean rows for the Library context panel's "Playlist Intelligence" overview when the source is
/// the whole library ("All Tracks") or Liked Songs rather than one playlist. Those views have no
/// PlaylistTracks rows of their own (All Tracks is LibraryEntries; its pseudo-project Id is
/// Guid.Empty), so the overview used to read zero tracks and showed "No tracks in this playlist
/// yet" over a 3,267-track library. Only the columns the overview uses are read: no waveform
/// blobs, no audio-features join.
/// </summary>
public static class LibraryOverviewQuery
{
    public static async Task<List<PlaylistTrack>> LoadAsync(AppDbContext db, bool likedOnly, CancellationToken ct = default)
    {
        var query = db.LibraryEntries.AsNoTracking();
        if (likedOnly) query = query.Where(e => e.IsLiked);

        var rows = await query.Select(e => new
        {
            e.UniqueHash,
            e.Artist,
            e.Title,
            e.DurationSeconds,
            e.CanonicalDuration,
            e.BPM,
            e.Energy,
            e.MusicalKey,
            e.DetectedSubGenre,
            e.PrimaryGenre,
            e.Genres,
        }).ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => new PlaylistTrack
        {
            PlaylistId = Guid.Empty,
            TrackUniqueHash = r.UniqueHash,
            Artist = r.Artist ?? string.Empty,
            Title = r.Title ?? string.Empty,
            CanonicalDuration = r.CanonicalDuration is > 0 ? r.CanonicalDuration : r.DurationSeconds is > 0 ? r.DurationSeconds * 1000 : null,
            BPM = r.BPM,
            Energy = r.Energy,
            MusicalKey = r.MusicalKey,
            DetectedSubGenre = r.DetectedSubGenre,
            PrimaryGenre = r.PrimaryGenre,
            Genres = r.Genres,
        }).ToList();
    }
}
