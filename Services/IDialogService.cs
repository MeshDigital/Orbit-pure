using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public interface IDialogService
{
    // Phase 23: Smart Crate Editor
    Task<Data.Entities.SmartCrateDefinitionEntity?> ShowSmartCrateEditorAsync(ViewModels.Library.SmartCrateEditorViewModel vm);

    /// <summary>
    /// Shows a confirmation dialog with Yes/No options.
    /// </summary>
    /// <returns>True if confirmed (Yes), False otherwise.</returns>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Yes", string cancelLabel = "No");

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
    /// Shows a folder selection dialog.
    /// </summary>
    /// <returns>Selected folder path or null if cancelled.</returns>
    Task<string?> OpenFolderDialogAsync(string title);
}
