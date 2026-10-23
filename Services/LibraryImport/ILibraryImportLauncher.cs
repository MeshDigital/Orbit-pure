using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

/// <summary>
/// The one way to start a folder import the user can see: Library folders "Add folder" /
/// "Scan all", Settings → Library, the setup wizard's "Scan now" and Library Health all go
/// through this, which opens the import sheet (implemented by <c>LibraryImportViewModel</c>).
/// Only background watch-folder imports bypass it.
/// </summary>
public interface ILibraryImportLauncher
{
    bool IsImportRunning { get; }

    /// <summary>Opens the folder picker, adds the folder as a Library Source and starts importing it.</summary>
    Task<bool> PickFolderAndStartImportAsync(bool watch = false);

    /// <summary>Adds <paramref name="path"/> as a Library Source (if needed) and starts importing it.</summary>
    Task<bool> AddFolderAndStartImportAsync(string path, bool? watch = null);

    /// <summary>Starts an import and returns once it has started (false when one is already running).</summary>
    Task<bool> StartImportAsync(IReadOnlyList<Guid>? folderIds = null, string? title = null);

    /// <summary>
    /// Starts an import that runs in the background from the start (activity chip, no locking
    /// sheet): the setup wizard keeps going while it runs.
    /// </summary>
    Task<bool> StartBackgroundImportAsync(IReadOnlyList<Guid>? folderIds = null, string? title = null) => StartImportAsync(folderIds, title);

    /// <summary>Runs an import in the sheet and returns its result once it ends (null when one is already running).</summary>
    Task<LibraryImportResult?> RunImportAsync(IReadOnlyList<Guid>? folderIds = null, string? title = null);
}
