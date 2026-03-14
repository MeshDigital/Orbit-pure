using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.LibraryActions;

/// <summary>
/// Deletes an entire playlist from the library
/// </summary>
public class DeletePlaylistAction : ILibraryAction
{
    private readonly ILogger<DeletePlaylistAction> _logger;
    private readonly ILibraryService _libraryService;

    public string Name => "Delete Playlist";
    public string IconGlyph => "❌";
    public string Category => "Playlist";

    public DeletePlaylistAction(
        ILogger<DeletePlaylistAction> logger,
        ILibraryService libraryService)
    {
        _logger = logger;
        _libraryService = libraryService;
    }

    public bool CanExecute(LibraryContext context)
    {
        return context.SelectedPlaylist != null;
    }

    public async Task ExecuteAsync(LibraryContext context)
    {
        if (context.SelectedPlaylist == null)
            return;

        try
        {
            var playlistTitle = context.SelectedPlaylist.SourceTitle;
            var playlistId = context.SelectedPlaylist.Id;

            _logger.LogInformation("Deleting playlist {Title} ({Id})", playlistTitle, playlistId);

            // Soft delete via LibraryService (will trigger reactive removal from UI)
            await _libraryService.DeletePlaylistJobAsync(playlistId);

            _logger.LogInformation("Successfully deleted playlist {Title}", playlistTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete playlist");
        }
    }
}
