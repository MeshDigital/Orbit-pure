using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Repositories;

namespace SLSKDONET.Services.Sets;

/// <summary>A transition saved for the same two tracks somewhere else, and where.</summary>
public sealed record TransitionFromHistory(PlaylistTrackTransition Transition, string SourceName, bool IsSet);

/// <summary>
/// Finds the most recently saved transition for a track pair across every playlist and set
/// (rebuild plan P3.4), so a pair met again starts from what the DJ designed last time.
/// </summary>
public interface ITransitionHistoryService
{
    /// <summary>Latest saved A→B transition by track hash, excluding the pair being edited; null when none.</summary>
    Task<TransitionFromHistory?> FindLatestAsync(string fromHash, string toHash, Guid excludeOutgoingId, Guid excludeIncomingId);
}

public sealed class TransitionHistoryService : ITransitionHistoryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public TransitionHistoryService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<TransitionFromHistory?> FindLatestAsync(string fromHash, string toHash, Guid excludeOutgoingId, Guid excludeIncomingId)
    {
        if (string.IsNullOrEmpty(fromHash) || string.IsNullOrEmpty(toHash))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var fromPlaylists = await (
                from t in db.PlaylistTrackTransitions.AsNoTracking()
                join a in db.PlaylistTracks.AsNoTracking() on t.OutgoingPlaylistTrackId equals a.Id
                join b in db.PlaylistTracks.AsNoTracking() on t.IncomingPlaylistTrackId equals b.Id
                join p in db.Projects.AsNoTracking() on t.PlaylistId equals p.Id into playlists
                from p in playlists.DefaultIfEmpty()
                where a.TrackUniqueHash == fromHash && b.TrackUniqueHash == toHash
                select new { Entity = t, Name = p != null ? p.SourceTitle : "a playlist" })
            .ToListAsync()
            .ConfigureAwait(false);

        var fromSets = await (
                from t in db.SetTrackTransitions.AsNoTracking()
                join a in db.SetTracks.AsNoTracking() on t.OutgoingSlotId equals a.Id
                join b in db.SetTracks.AsNoTracking() on t.IncomingSlotId equals b.Id
                join s in db.SetLists.AsNoTracking() on t.SetId equals s.Id
                where a.TrackUniqueHash == fromHash && b.TrackUniqueHash == toHash
                select new { Entity = t, s.Name })
            .ToListAsync()
            .ConfigureAwait(false);

        // Newest first, compared as DateTimes (SQLite text ordering is unreliable across formats).
        return fromPlaylists
            .Select(x => new TransitionFromHistory(TransitionRepository.ToDomain(x.Entity), x.Name, IsSet: false))
            .Concat(fromSets.Select(x => new TransitionFromHistory(SetTransitionRepository.ToDomain(x.Entity), x.Name, IsSet: true)))
            .Where(h => !(h.Transition.OutgoingPlaylistTrackId == excludeOutgoingId && h.Transition.IncomingPlaylistTrackId == excludeIncomingId))
            .OrderByDescending(h => h.Transition.UpdatedAtUtc)
            .FirstOrDefault();
    }
}
