using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SLSKDONET.Models.Timeline;

namespace SLSKDONET.Services.Repositories;

/// <summary>
/// Persists Mix-style transition configuration for adjacent track pairs within a playlist.
/// See <see cref="PlaylistTrackTransition"/>.
/// </summary>
public interface ITransitionRepository
{
    Task<PlaylistTrackTransition?> GetTransitionAsync(Guid outgoingPlaylistTrackId, Guid incomingPlaylistTrackId);
    Task<List<PlaylistTrackTransition>> GetTransitionsForPlaylistAsync(Guid playlistId);
    Task UpsertTransitionAsync(PlaylistTrackTransition transition);
    Task DeleteTransitionAsync(Guid outgoingPlaylistTrackId, Guid incomingPlaylistTrackId);
}
