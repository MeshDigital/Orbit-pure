using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Utils;

namespace SLSKDONET.Services;

/// <summary>
/// Concrete implementation of IFilePathResolverService.
/// Extracted from LibraryService to adhere to Single Responsibility Principle.
/// </summary>
public class FilePathResolverService : IFilePathResolverService
{
    private readonly ILogger<FilePathResolverService> _logger;
    private readonly AppConfig _appConfig;

    public FilePathResolverService(ILogger<FilePathResolverService> logger, AppConfig appConfig)
    {
        _logger = logger;
        _appConfig = appConfig;
    }

    /// <inheritdoc/>
    public async Task<string?> ResolveMissingFilePathAsync(LibraryEntry missingTrack)
    {
        if (!_appConfig.EnableFilePathResolution)
        {
            _logger.LogDebug("File path resolution is disabled in configuration");
            return null;
        }

        if (_appConfig.LibraryRootPaths == null || !_appConfig.LibraryRootPaths.Any())
        {
            _logger.LogWarning("No library root paths configured for file resolution");
            return null;
        }

        // Step 0: Fast check - does the original path still exist?
        if (File.Exists(missingTrack.FilePath))
        {
            return missingTrack.FilePath;
        }

        _logger.LogInformation("Attempting to resolve missing file: {Artist} - {Title} (Original: {Path})", 
            missingTrack.Artist, missingTrack.Title, missingTrack.FilePath);

        // Step 1: DIRECT FILENAME MATCH
        // Case: File moved but kept the same filename
        string oldFileName = Path.GetFileName(missingTrack.FilePath);
        string? resolvedPath = await Task.Run(() => SearchByFilename(oldFileName, _appConfig.LibraryRootPaths)).ConfigureAwait(false);
        
        if (resolvedPath != null)
        {
            _logger.LogInformation("Resolved via filename match: {Path}", resolvedPath);
            return resolvedPath;
        }

        // Step 2: FUZZY METADATA MATCH
        // Case: File moved AND renamed, or slight metadata differences
        resolvedPath = await Task.Run(() => SearchByFuzzyMetadata(missingTrack, _appConfig.LibraryRootPaths)).ConfigureAwait(false);
        
        if (resolvedPath != null)
        {
            _logger.LogInformation("Resolved via fuzzy metadata match: {Path}", resolvedPath);
            return resolvedPath;
        }

        _logger.LogWarning("Could not resolve missing file: {Artist} - {Title}", 
            missingTrack.Artist, missingTrack.Title);
        return null;
    }

    /// <summary>
    /// Searches for an exact filename match in the configured library root paths.
    /// </summary>
    private string? SearchByFilename(string fileName, IEnumerable<string> rootPaths)
    {
        foreach (string rootPath in rootPaths)
        {
            if (!Directory.Exists(rootPath))
            {
                _logger.LogWarning("Library root path does not exist: {Path}", rootPath);
                continue;
            }

            try
            {
                // Use EnumerateFiles for potentially faster/lazy search
                var foundPath = Directory.EnumerateFiles(rootPath, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (foundPath != null)
                {
                    return foundPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching directory: {Path}", rootPath);
            }
        }

        return null;
    }

    /// <summary>
    /// Searches for files using fuzzy metadata matching based on Artist and Title.
    /// Uses Levenshtein distance to find the best match above the configured threshold.
    /// </summary>
    private string? SearchByFuzzyMetadata(LibraryEntry missingTrack, IEnumerable<string> rootPaths)
    {
        // Skip fuzzy matching if metadata is missing
        if (string.IsNullOrWhiteSpace(missingTrack.Artist) || string.IsNullOrWhiteSpace(missingTrack.Title))
        {
            _logger.LogDebug("Skipping fuzzy match - insufficient metadata");
            return null;
        }

        string targetMetadata = $"{missingTrack.Artist} - {missingTrack.Title}";
        string? bestMatchPath = null;
        double bestMatchScore = _appConfig.FuzzyMatchThreshold;

        // Common music file extensions
        string[] musicExtensions = { ".mp3", ".flac", ".m4a", ".ogg", ".wav", ".wma", ".aac" };

        foreach (string rootPath in rootPaths)
        {
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            try
            {
                // Enumerate all potential music files
                var allFiles = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => musicExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

                foreach (string filePath in allFiles)
                {
                    // Use filename as metadata proxy (faster than reading tags from every file)
                    string currentMetadata = Path.GetFileNameWithoutExtension(filePath);
                    double score = StringDistanceUtils.GetNormalizedMatchScore(targetMetadata, currentMetadata);

                    if (score > bestMatchScore)
                    {
                        bestMatchScore = score;
                        bestMatchPath = filePath;
                        
                        _logger.LogDebug("Found potential match: {Path} (score: {Score:F2})", filePath, score);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during fuzzy search in: {Path}", rootPath);
            }
        }

        if (bestMatchPath != null)
        {
            _logger.LogInformation("Fuzzy match found with score {Score:F2}: {Path}", bestMatchScore, bestMatchPath);
        }

        return bestMatchPath;
    }
}
