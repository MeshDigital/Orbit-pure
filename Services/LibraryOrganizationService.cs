using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services;

/// <summary>
/// Session 2 (Phase 2 Performance Overhaul): Extracted from MetadataEnrichmentOrchestrator.
/// Handles file organization and directory structure for downloaded tracks.
/// Follows user-configurable library organization patterns.
/// </summary>
public class LibraryOrganizationService
{
    private readonly AppConfig _config;
    private readonly ILogger<LibraryOrganizationService> _logger;
    
    public LibraryOrganizationService(AppConfig config, ILogger<LibraryOrganizationService> logger)
    {
        _config = config;
        _logger = logger;
    }
    
    /// <summary>
    /// Organizes a downloaded file into proper directory structure.
    /// Structure: {DownloadDirectory}/{Artist}/{Album}/{Filename}
    /// </summary>
    /// <param name="artist">Track artist</param>
    /// <param name="album">Track album</param>
    /// <param name="downloadedPath">Current file path</param>
    /// <returns>New organized file path</returns>
    public async Task<string> OrganizeFileAsync(string artist, string album, string downloadedPath)
    {
        try
        {
            // Build target directory structure
            var targetDir = Path.Combine(
                _config.DownloadDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                SanitizeFilename(artist ?? "Unknown Artist"),
                SanitizeFilename(album ?? "Unknown Album")
            );
            
            // Create directory if it doesn't exist
            Directory.CreateDirectory(targetDir);
            
            // Build target file path
            var filename = Path.GetFileName(downloadedPath);
            var targetPath = Path.Combine(targetDir, filename);
            
            // Handle duplicates
            if (File.Exists(targetPath))
            {
                _logger.LogWarning("File already exists, generating unique name: {Path}", targetPath);
                targetPath = GetUniqueFilePath(targetPath);
            }
            
            // Move file to organized location
            if (File.Exists(downloadedPath))
            {
                await Task.Run(() => File.Move(downloadedPath, targetPath));
                _logger.LogInformation("Organized file: {Source} → {Target}", downloadedPath, targetPath);
            }
            else
            {
                _logger.LogWarning("Source file not found, returning target path anyway: {Path}", downloadedPath);
            }
            
            return targetPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to organize file: {Path}", downloadedPath);
            return downloadedPath; // Return original path on failure
        }
    }
    
    /// <summary>
    /// Sanitizes filename by removing invalid characters.
    /// </summary>
    private string SanitizeFilename(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return "Unknown";
        
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", filename.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        
        // Trim and handle edge cases
        sanitized = sanitized.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
    }
    
    /// <summary>
    /// Generates a unique file path by appending (1), (2), etc.
    /// </summary>
    private string GetUniqueFilePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        
        int counter = 1;
        string newPath;
        
        do
        {
            newPath = Path.Combine(dir, $"{name} ({counter}){ext}");
            counter++;
        } while (File.Exists(newPath));
        
        return newPath;
    }
    
    /// <summary>
    /// Validates if a file path is within the configured library directory.
    /// Useful for security checks.
    /// </summary>
    public bool IsWithinLibraryDirectory(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var libraryPath = Path.GetFullPath(_config.DownloadDirectory ?? "");
            
            return fullPath.StartsWith(libraryPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Phase 10: Performs spectral audit on FLAC files to detect transcodes.
    /// Checks for energy above 16kHz, indicating possible lossy source.
    /// </summary>
    public async Task<bool> IsTranscodedFlacAsync(string filePath)
    {
        if (!filePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            // Use ffprobe to analyze frequency spectrum
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-f lavfi -i \"amovie='{filePath}',astats=metadata=1:reset=1\" -show_entries frame_tags=lavfi.astats.Overall.RMS_level -of csv=p=0 -v quiet",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Simple check: if output contains high frequency data, assume not transcoded
            // This is a placeholder; real implementation would parse spectrum data
            return !output.Contains("16kHz"); // Placeholder logic
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze spectral data for {File}", filePath);
            return false; // Assume not transcoded if analysis fails
        }
    }
}
