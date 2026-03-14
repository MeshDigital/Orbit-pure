using System.Threading.Tasks;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.LibraryActions;

/// <summary>
/// Interface for Library action plugins.
/// Each action represents something you can do to a playlist or track (open folder, delete, etc.)
/// </summary>
public interface ILibraryAction
{
    /// <summary>
    /// Display name shown in UI (e.g., "Open Folder", "Remove from Playlist")
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Icon or emoji for the button
    /// </summary>
    string IconGlyph { get; }

    /// <summary>
    /// Category for grouping actions
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Check if this action can be executed in the current context
    /// </summary>
    bool CanExecute(LibraryContext context);

    /// <summary>
    /// Execute the action
    /// </summary>
    Task ExecuteAsync(LibraryContext context);
}
