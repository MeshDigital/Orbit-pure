using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.LibraryActions;

/// <summary>
/// Removes selected tracks from the current playlist
/// (Keeps them in global all-tracks view)
/// </summary>
public class RemoveFromPlaylistAction : ILibraryAction
{
    private readonly ILogger<RemoveFromPlaylistAction> _logger;
    private readonly DownloadManager _downloadManager;

    public string Name => "Remove from Playlist";
    public string IconGlyph => "🗑️";
    public string Category => "Playlist";

    public RemoveFromPlaylistAction(
        ILogger<RemoveFromPlaylistAction> logger,
        DownloadManager downloadManager)
    {
        _logger = logger;
        _downloadManager = downloadManager;
    }

    public bool CanExecute(LibraryContext context)
    {
        return context.SelectedPlaylist != null && context.SelectedTracks.Any();
    }

    public async Task ExecuteAsync(LibraryContext context)
    {
        if (context.SelectedPlaylist == null || !context.SelectedTracks.Any())
            return;

        try
        {
            _logger.LogInformation("Removing {Count} tracks from playlist {Title}", 
                context.SelectedTracks.Count, context.SelectedPlaylist.SourceTitle);

            foreach (var track in context.SelectedTracks)
            {
                // Remove from playlist tracks collection
                var trackToRemove = context.SelectedPlaylist.PlaylistTracks
                    .FirstOrDefault(pt => pt.Id == track.Model.Id);
                
                if (trackToRemove != null)
                {
                    context.SelectedPlaylist.PlaylistTracks.Remove(trackToRemove);
                }
                
                // NOTE: Track remains in _downloadManager.AllGlobalTracks (all downloads view)
            }

            // Refresh playlist counts
            context.SelectedPlaylist.RefreshStatusCounts();

            _logger.LogInformation("Successfully removed tracks from playlist");
            
            // Trigger UI reload by reselecting the playlist
            if (context.ViewModel != null)
            {
                var currentPlaylist = context.ViewModel.SelectedProject;
                context.ViewModel.SelectedProject = null;
                context.ViewModel.SelectedProject = currentPlaylist;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove tracks from playlist");
        }

        await Task.CompletedTask;
    }
}
