using System;
using System.Text.Json;
using SLSKDONET.Data;

namespace SLSKDONET.Services;

internal static class IngestionActivityLogFactory
{
    public static PlaylistActivityLogEntity Create(Guid playlistId, string action, object details, DateTime? timestampUtc = null)
    {
        return new PlaylistActivityLogEntity
        {
            Id = Guid.NewGuid(),
            PlaylistId = playlistId,
            Action = action,
            Details = JsonSerializer.Serialize(details),
            Timestamp = timestampUtc ?? DateTime.UtcNow,
        };
    }
}
