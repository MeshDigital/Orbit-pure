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
    private readonly IAudioIntegrityService _integrityService;
    private readonly ILibraryService _libraryService;
    
    public LibraryOrganizationService(
        AppConfig config, 
        ILogger<LibraryOrganizationService> logger,
        IAudioIntegrityService integrityService,
        ILibraryService libraryService)
    {
        _config = config;
        _logger = logger;
        _integrityService = integrityService;
        _libraryService = libraryService;
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
            
            // Phase 10: Spectral FLAC auditing - Check integrity after organization
            try
            {
                var isTranscoded = await IsTranscodedFlacAsync(targetPath);
                if (isTranscoded)
                {
                    // For now, we'll mark the file but can't update LibraryEntry without title
                    // This will be handled by a background scan later
                    _logger.LogWarning("⚠️ Forensic verdict: {File} detected as transcoded FLAC", Path.GetFileName(targetPath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to perform spectral audit on organized file: {Path}", targetPath);
                // Don't fail the organization process for audit failures
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
    /// Generates a unique hash for a track based on artist and title.
    /// Used to identify tracks in the library index.
    /// </summary>
    private string GenerateUniqueHash(string artist, string title)
    {
        var key = $"{artist}-{title}".ToLowerInvariant().Trim();
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Phase 10: Performs spectral audit on FLAC files to detect transcodes.
    /// Uses FFT analysis to check for energy above 16kHz, indicating possible lossy source.
    /// </summary>
    public async Task<bool> IsTranscodedFlacAsync(string filePath)
    {
        if (!filePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var result = await _integrityService.AnalyseAsync(filePath);
            return !result.IsGenuineLossless;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze spectral integrity for {File}", filePath);
            return false; // Assume not transcoded if analysis fails
        }
    }
}
