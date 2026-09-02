using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Downloads;

namespace SLSKDONET.Services;

public interface IDialogService
{
    // Phase 23: Smart Crate Editor
    Task<Data.Entities.SmartCrateDefinitionEntity?> ShowSmartCrateEditorAsync(ViewModels.Library.SmartCrateEditorViewModel vm);

    /// <summary>
    /// Shows the "New Smart Playlist" dialog for building a criteria-based (BPM/Energy/
    /// Valence/Danceability/Rating/Liked) smart playlist. Returns null if cancelled.
    /// </summary>
    Task<(string Name, SmartPlaylistCriteria Criteria)?> ShowCreateSmartPlaylistAsync();

    /// <summary>
    /// Shows a confirmation dialog with Yes/No options.
    /// </summary>
    /// <returns>True if confirmed (Yes), False otherwise.</returns>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Yes", string cancelLabel = "No");

    /// <summary>
    /// Shows the "Remove Track" choice dialog — lets the user pick between removing a track
    /// from just the current playlist (keeping it in the library) or deleting it entirely from
    /// disk and history. <paramref name="canRemoveFromPlaylist"/> should be false when there's
    /// no specific playlist in context (e.g. the All Tracks view), which hides that option.
    /// </summary>
    Task<Views.Avalonia.Controls.RemoveTrackChoice> ShowRemoveTrackChoiceAsync(string trackLabel, bool canRemoveFromPlaylist, string? playlistName);

    /// <summary>
    /// Shows a simple alert dialog.
    /// </summary>
    Task ShowAlertAsync(string title, string message);
    
    /// <summary>
    /// Shows a Save File dialog.
    /// </summary>
    /// <returns>Selected file path or null if cancelled.</returns>
    Task<string?> SaveFileAsync(string title, string defaultFileName, string extension = "xml");


    /// <summary>
    /// Shows a prompt dialog for text input.
    /// </summary>
    Task<string?> ShowPromptAsync(string title, string message, string initialValue = "");

    /// <summary>
    /// Shows a project picker dialog.
    /// </summary>
    Task<PlaylistJob?> ShowProjectPickerAsync(System.Collections.Generic.IEnumerable<PlaylistJob> projects);

    /// <summary>
    /// Shows a playlist picker dialog that supports creating new playlists.
    /// </summary>
    Task<ViewModels.Library.PlaylistPickerResult?> ShowPlaylistPickerDialogAsync(System.Collections.Generic.IEnumerable<PlaylistJob> playlists);

    /// <summary>
    /// Shows the "Combine Playlists" dialog — lets the user pick 2+ playlists, a name for the
    /// combined playlist, and whether to dedupe overlapping tracks. <paramref name="preSelected"/>
    /// pre-checks playlists handed off from another entry point (e.g. the Library sidebar).
    /// </summary>
    Task<ViewModels.Library.CombinePlaylistsResult?> ShowCombinePlaylistsDialogAsync(
        System.Collections.Generic.IEnumerable<PlaylistJob> playlists,
        System.Collections.Generic.IReadOnlyList<PlaylistJob>? preSelected = null);

    /// <summary>
    /// Shows a batch tag editor dialog.
    /// </summary>
    Task<ViewModels.Library.BatchTagEditResult?> ShowBatchTagEditDialogAsync(string? initialFileName = null);

    /// <summary>
    /// Shows the bulk-rename-by-pattern dialog for <paramref name="trackCount"/> selected tracks,
    /// previewing the pattern against up to a few sample tracks.
    /// </summary>
    Task<ViewModels.Library.BulkRenameResult?> ShowBulkRenameDialogAsync(
        int trackCount, System.Collections.Generic.IReadOnlyList<ViewModels.Library.BulkRenamePreviewTrack> previewTracks);

    /// <summary>
    /// Shows the Move/Copy mode-choice dialog for <paramref name="trackCount"/> selected tracks.
    /// The destination folder is picked separately afterward via <see cref="OpenFolderDialogAsync"/>.
    /// </summary>
    Task<ViewModels.Library.BulkMoveOrCopyResult?> ShowBulkMoveOrCopyDialogAsync(int trackCount);

    /// <summary>
    /// Shows a folder selection dialog.
    /// </summary>
    /// <returns>Selected folder path or null if cancelled.</returns>
    Task<string?> OpenFolderDialogAsync(string title);

    Task ShowSuggestedFlowImpactAsync(SuggestedFlowImpactViewModel vm);

    Task ShowSpectralForensicsAsync(UnifiedTrackViewModel vm);
}
