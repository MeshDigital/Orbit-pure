using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.LibraryActions;

/// <summary>
/// Opens Windows Explorer to the folder containing the downloaded track file
/// </summary>
public class OpenFolderAction : ILibraryAction
{
    private readonly ILogger<OpenFolderAction> _logger;

    public string Name => "Open Folder";
    public string IconGlyph => "📁";
    public string Category => "File";

    public OpenFolderAction(ILogger<OpenFolderAction> logger)
    {
        _logger = logger;
    }

    public bool CanExecute(LibraryContext context)
    {
        // Only enabled if at least one selected track has a downloaded file
        return context.SelectedTracks.Any(t => 
            !string.IsNullOrEmpty(t.Model.ResolvedFilePath) && 
            File.Exists(t.Model.ResolvedFilePath));
    }

    public Task ExecuteAsync(LibraryContext context)
    {
        try
        {
            var trackWithFile = context.SelectedTracks.FirstOrDefault(t => 
                !string.IsNullOrEmpty(t.Model.ResolvedFilePath) && 
                File.Exists(t.Model.ResolvedFilePath));

            if (trackWithFile == null)
            {
                _logger.LogWarning("No downloaded track found to open folder");
                return Task.CompletedTask;
            }

            var filePath = trackWithFile.Model.ResolvedFilePath;
            
            // Use /select to highlight the file in Explorer
            _logger.LogInformation("Opening folder and selecting file: {File}", filePath);
            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open folder");
        }

        return Task.CompletedTask;
    }
}
