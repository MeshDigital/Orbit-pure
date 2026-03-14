using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using SLSKDONET.Views.Avalonia.Controls;
using Avalonia.Threading;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public class DialogService : IDialogService
{
    private Window? GetOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Yes", string cancelLabel = "No")
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new ConfirmationDialog(title, message, confirmLabel, cancelLabel);
            var owner = GetOwnerWindow();

            if (owner != null)
            {
                await dialog.ShowDialog(owner);
            }
            else
            {
                // Fallback: If no owner (should unlikely happen in this flow), try to show standalone
               dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
               dialog.Show();
               // We can't await completion easily with Show(). 
               // Assuming MainWindow always exists for user interactions.
            }

            return dialog.IsConfirmed;
        });
    }

    public async Task ShowAlertAsync(string title, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
             var dialog = new ConfirmationDialog(title, message, "OK", "");
             // Hide No button for alert
             var noBtn = dialog.FindControl<Button>("NoButton");
             if (noBtn != null) noBtn.IsVisible = false;

             var owner = GetOwnerWindow();
             if (owner != null)
             {
                 await dialog.ShowDialog(owner);
             }
        });
    }

    public async Task<string?> SaveFileAsync(string title, string defaultFileName, string extension = "xml")
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = GetOwnerWindow();
            if (owner?.StorageProvider == null) return null;
            
            var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = defaultFileName,
                DefaultExtension = extension,
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType($"{extension.ToUpper()} File")
                    {
                        Patterns = new[] { $"*.{extension}" }
                    }
                }
            });

            return file?.Path.LocalPath;
        });
    }


    public async Task<Data.Entities.SmartCrateDefinitionEntity?> ShowSmartCrateEditorAsync(ViewModels.Library.SmartCrateEditorViewModel vm)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new Views.Avalonia.Dialogs.SmartCrateEditorDialog
            {
                DataContext = vm
            };
            
            var owner = GetOwnerWindow();
            if (owner != null)
            {
                var result = await dialog.ShowDialog<Data.Entities.SmartCrateDefinitionEntity?>(owner);
                return result;
            }
            
            return null;
        });
    }

    public async Task<string?> ShowPromptAsync(string title, string message, string initialValue = "")
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new PromptDialog(title, message, initialValue);
            var owner = GetOwnerWindow();
            if (owner != null)
            {
                return await dialog.ShowDialog<string?>(owner);
            }
            return null;
        });
    }

    public async Task<PlaylistJob?> ShowProjectPickerAsync(IEnumerable<PlaylistJob> projects)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new ProjectPickerDialog(projects);
            var owner = GetOwnerWindow();
            if (owner != null)
            {
                return await dialog.ShowDialog<PlaylistJob?>(owner);
            }
            return null;
        });
    }

    public async Task<string?> OpenFolderDialogAsync(string title)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = GetOwnerWindow();
            if (owner?.StorageProvider == null) return null;

            var folder = await owner.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });

            return folder?.FirstOrDefault()?.Path.LocalPath;
        });
    }
}
