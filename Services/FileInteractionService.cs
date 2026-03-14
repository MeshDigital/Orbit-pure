using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Visuals;
using Avalonia.Controls;

namespace SLSKDONET.Services;

public class FileInteractionService : IFileInteractionService
{
    private readonly IClassicDesktopStyleApplicationLifetime? _desktop;

    public FileInteractionService()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
        }
    }

    public async Task<string?> OpenFolderDialogAsync(string title)
    {
        if (_desktop?.MainWindow is not { } window)
        {
            return null;
        }

        var result = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }

    public async Task<string?> OpenFileDialogAsync(string title, IEnumerable<FileDialogFilter>? filters = null)
    {
        if (_desktop?.MainWindow is not { } window)
        {
            return null;
        }

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        if (filters != null)
        {
            var avaloniaFilters = new List<FilePickerFileType>();
            foreach (var filter in filters)
            {
                avaloniaFilters.Add(new FilePickerFileType(filter.Name)
                {
                    Patterns = filter.Extensions.Select(e => $"*.{e}").ToList()
                });
            }
            options.FileTypeFilter = avaloniaFilters;
        }

        var result = await window.StorageProvider.OpenFilePickerAsync(options);

        if (result.Count > 0)
        {
            var item = result[0];
            // Safe path extraction
            if (item.Path.IsAbsoluteUri && item.Path.Scheme == "file")
                return item.Path.LocalPath;
                
             return System.Uri.UnescapeDataString(item.Path.AbsolutePath);
        }

        return null;
    }

    public async Task<string?> SaveFileDialogAsync(string title, string defaultFileName, string? extension = null)
    {
        if (_desktop?.MainWindow is not { } window)
        {
            return null;
        }

        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName
        };

        if (!string.IsNullOrEmpty(extension))
        {
            options.FileTypeChoices = new List<FilePickerFileType>
            {
                new FilePickerFileType(extension.ToUpper())
                {
                    Patterns = new List<string> { $"*.{extension.ToLower()}" }
                }
            };
        }

        var result = await window.StorageProvider.SaveFilePickerAsync(options);

        return result?.Path.LocalPath;
    }

    public void RevealFileInExplorer(string filePath)

    {
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return;

        try
        {
            // Windows-specific for now, but this agent is running in Windows context per user metadata
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        catch (System.Exception ex)
        {
             // Log or ignore
             System.Diagnostics.Debug.WriteLine($"Failed to reveal file: {ex.Message}");
        }
    }
}
