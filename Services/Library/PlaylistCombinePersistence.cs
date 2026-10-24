using SLSKDONET.Models;

namespace SLSKDONET.Services.Library;

/// <summary>
/// Persists the arranged output of Combine Playlists as a new user playlist.
/// </summary>
public static class PlaylistCombinePersistence
{
    public static List<PlaylistTrack> CollectTracks(
        IEnumerable<IEnumerable<PlaylistTrack>> sourcePlaylists,
        bool skipDuplicateTracks)
    {
        ArgumentNullException.ThrowIfNull(sourcePlaylists);

        var combinedTracks = new List<PlaylistTrack>();
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var playlistTracks in sourcePlaylists)
        {
            foreach (var track in playlistTracks)
            {
                var hash = track.TrackUniqueHash ?? string.Empty;
                if (skipDuplicateTracks && hash.Length > 0 && !seenHashes.Add(hash))
                    continue;

                combinedTracks.Add(track);
            }
        }

        return combinedTracks;
    }

    public static async Task<PlaylistJob> SaveAsync(
        ILibraryService library,
        string name,
        IReadOnlyList<PlaylistTrack> orderedTracks)
    {
        ArgumentNullException.ThrowIfNull(library);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A playlist name is required.", nameof(name));
        if (orderedTracks == null || orderedTracks.Count == 0)
            throw new ArgumentException("At least one track is required to create a combined playlist.", nameof(orderedTracks));

        var playlist = await library.CreateEmptyPlaylistAsync(name.Trim());
        try
        {
            // Add the entire batch to the empty destination. This preserves repeated hashes
            // when the user has unchecked "Skip duplicate tracks" in the combine dialog.
            await library.AddTracksToProjectAsync(orderedTracks, playlist.Id);

            var persistedTracks = await library.LoadPlaylistTracksAsync(playlist.Id);
            if (persistedTracks.Count != orderedTracks.Count)
            {
                throw new InvalidOperationException(
                    $"Only {persistedTracks.Count} of {orderedTracks.Count} combined tracks were saved to \"{playlist.SourceTitle}\".");
            }

            var orderByHash = orderedTracks
                .Select((track, index) => new { track.TrackUniqueHash, index })
                .Where(item => !string.IsNullOrWhiteSpace(item.TrackUniqueHash))
                .GroupBy(item => item.TrackUniqueHash!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);

            var orderedPersistedTracks = persistedTracks
                .OrderBy(track => orderByHash.TryGetValue(track.TrackUniqueHash ?? string.Empty, out var index)
                    ? index
                    : orderByHash.Count + Math.Max(0, track.SortOrder))
                .ToList();

            for (var index = 0; index < orderedPersistedTracks.Count; index++)
            {
                orderedPersistedTracks[index].SortOrder = index + 1;
                orderedPersistedTracks[index].TrackNumber = index + 1;
            }

            await library.SaveTrackOrderAsync(playlist.Id, orderedPersistedTracks);
            playlist.PlaylistTracks = orderedPersistedTracks;
            playlist.TotalTracks = orderedPersistedTracks.Count;
            return playlist;
        }
        catch (Exception saveException)
        {
            try
            {
                // Avoid leaving an empty/partially-populated playlist behind when adding tracks
                // or persisting their arranged order fails.
                await library.DeletePlaylistJobAsync(playlist.Id);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    $"Could not save combined playlist \"{playlist.SourceTitle}\"; cleanup also failed.",
                    saveException,
                    cleanupException);
            }

            throw;
        }
    }
}
