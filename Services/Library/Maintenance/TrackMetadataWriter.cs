using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>
/// Writes metadata changes (keyed by track hash) to the three denormalized tables that carry the
/// same fields — <c>LibraryEntries</c>, <c>PlaylistTracks</c> and <c>Tracks</c> — so a maintenance
/// fix or batch edit stays consistent everywhere. Pure persistence: no TagLib, no events.
/// <para>
/// Genre is stored canonically as <c>PrimaryGenre</c> plus a JSON array in <c>Genres</c> (the batch
/// editor's format). Readers must keep accepting the legacy comma-joined <c>Genres</c> string too.
/// </para>
/// </summary>
public sealed class TrackMetadataWriter
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<TrackMetadataWriter> _logger;

    public TrackMetadataWriter(IDbContextFactory<AppDbContext> dbFactory, ILogger<TrackMetadataWriter> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>One field change to persist for a track. <see cref="Value"/> == null clears it.</summary>
    public sealed record Change(TagField Field, string? Value);

    /// <summary>
    /// Applies the changes to every row with <paramref name="uniqueHash"/> across the three tables
    /// inside <paramref name="db"/> (caller saves). Fields with no DB column — AlbumArtist, Remixer —
    /// are file-only and skipped. Returns the DB fields actually written.
    /// </summary>
    public async Task<IReadOnlyList<TagField>> ApplyAsync(AppDbContext db, string uniqueHash, IReadOnlyList<Change> changes, CancellationToken ct = default)
    {
        var written = new List<TagField>();
        if (string.IsNullOrWhiteSpace(uniqueHash) || changes.Count == 0)
            return written;

        var entries = await db.LibraryEntries.Where(e => e.UniqueHash == uniqueHash).ToListAsync(ct).ConfigureAwait(false);
        var playlistTracks = await db.PlaylistTracks.Where(t => t.TrackUniqueHash == uniqueHash).ToListAsync(ct).ConfigureAwait(false);
        var tracks = await db.Tracks.Where(t => t.GlobalId == uniqueHash).ToListAsync(ct).ConfigureAwait(false);

        foreach (var change in changes)
        {
            var value = Normalize(change.Value);
            switch (change.Field)
            {
                case TagField.Title:
                    foreach (var e in entries) e.Title = value ?? string.Empty;
                    foreach (var p in playlistTracks) p.Title = value ?? string.Empty;
                    foreach (var t in tracks) t.Title = value ?? string.Empty;
                    written.Add(TagField.Title);
                    break;

                case TagField.Artist:
                    foreach (var e in entries) e.Artist = value ?? string.Empty;
                    foreach (var p in playlistTracks) p.Artist = value ?? string.Empty;
                    foreach (var t in tracks) t.Artist = value ?? string.Empty;
                    written.Add(TagField.Artist);
                    break;

                case TagField.Album:
                    foreach (var e in entries) e.Album = value ?? string.Empty;
                    foreach (var p in playlistTracks) p.Album = value ?? string.Empty;
                    // TrackEntity has no Album column.
                    written.Add(TagField.Album);
                    break;

                case TagField.Genre:
                    var json = GenreJson(value);
                    foreach (var e in entries) { e.PrimaryGenre = value; e.Genres = json; }
                    foreach (var p in playlistTracks) { p.PrimaryGenre = value; p.Genres = json; }
                    foreach (var t in tracks) { t.PrimaryGenre = value; t.Genres = json; }
                    written.Add(TagField.Genre);
                    break;

                case TagField.Label:
                    foreach (var e in entries) e.Label = value;
                    foreach (var p in playlistTracks) p.Label = value;
                    foreach (var t in tracks) t.Label = value;
                    written.Add(TagField.Label);
                    break;

                default:
                    // AlbumArtist / Remixer have no DB column; they live only in the file tags.
                    break;
            }
        }

        return written;
    }

    /// <summary>Opens a context, applies, saves, and disposes — for callers without a live context.</summary>
    public async Task<IReadOnlyList<TagField>> ApplyAndSaveAsync(string uniqueHash, IReadOnlyList<Change> changes, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var written = await ApplyAsync(db, uniqueHash, changes, ct).ConfigureAwait(false);
        if (written.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Wrote {Fields} for {Hash} across LibraryEntries/PlaylistTracks/Tracks", string.Join(",", written), uniqueHash);
        }
        return written;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GenreJson(string? value) => value == null ? "[]" : JsonSerializer.Serialize(new[] { value });
}
