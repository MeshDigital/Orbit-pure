using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Provides centralized path management with filesystem-safe slug generation.
/// Prevents "Invalid character" crashes on Windows/Linux by sanitizing artist/album names.
/// Future-proof for "Move Library" and "Rename Pattern" features.
/// </summary>
public class PathProviderService
{
    private readonly AppConfig _config;
    private readonly FileNameFormatter _fileNameFormatter;
    private readonly ILogger<PathProviderService> _logger;

    public PathProviderService(AppConfig config, FileNameFormatter fileNameFormatter, ILogger<PathProviderService> logger)
    {
        _config = config;
        _fileNameFormatter = fileNameFormatter;
        _logger = logger;
    }

    /// <summary>
    /// Gets the full path for a track using either the custom template or default Artist/Album structure.
    /// </summary>
    public string GetTrackPath(Track track)
    {
        var template = _config.NameFormat;
        if (string.IsNullOrWhiteSpace(template))
        {
            // Fallback to legacy hardcoded structure: Artist/Album/Title.ext
            return GetTrackPath(track.Artist ?? "Unknown", track.Album ?? "Unknown", track.Title ?? "Unknown", track.GetExtension());
        }

        var formatted = _fileNameFormatter.Format(template, track);
        var extension = track.GetExtension();
        
        // Handle potential subfolders in template (e.g. "{artist}/{title}")
        var segments = formatted.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var safeSegments = segments.Select(Slugify).ToArray();
        
        var relativePath = Path.Combine(safeSegments) + "." + extension;
        var fullPath = ResolveWithinDownloadDirectory(relativePath);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null) Directory.CreateDirectory(directory);

        return fullPath;
    }

    /// <summary>
    /// Legacy support for explicit string arguments.
    /// </summary>
    public string GetTrackPath(string artist, string album, string title, string extension)
    {
        var safeArtist = Slugify(artist);
        var safeAlbum = Slugify(album);
        var safeTitle = Slugify(title);

        var fullPath = ResolveWithinDownloadDirectory(Path.Combine(safeArtist, safeAlbum, $"{safeTitle}.{extension}"));

        // Ensure directory exists
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null) Directory.CreateDirectory(directory);

        return fullPath;
    }

    /// <summary>
    /// Removes invalid filesystem characters and replaces with safe alternatives.
    /// Prevents crashes on Windows/Linux from characters like ?, :, *, <, >, |
    /// </summary>
    /// <param name="input">Raw string (artist, album, or title)</param>
    /// <returns>Filesystem-safe string</returns>
    private string Slugify(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "Unknown";

        // Invalid characters for Windows/Linux filesystems
        var invalidChars = new[] { '?', ':', '*', '<', '>', '|', '"', '/', '\\' };

        var result = input;
        foreach (var c in invalidChars)
        {
            result = result.Replace(c, '_');
        }

        // Strip control characters — a peer could embed them to craft confusing names.
        result = new string(result.Where(c => !char.IsControl(c)).ToArray());

        // Trim and limit length to prevent path length issues
        result = result.Trim();
        if (result.Length > 200)
        {
            result = result.Substring(0, 200);
            _logger.LogWarning("Truncated long filename from {Original} to {Truncated}", input.Length, 200);
        }

        if (string.IsNullOrWhiteSpace(result)) return "Unknown";

        // Neutralize dot-only segments ("." / ".." / "...") so a peer-influenced artist/album
        // can never escape the download directory through path traversal.
        if (result.All(c => c == '.'))
            return "_";

        // Windows ignores trailing dots and spaces, so strip them to avoid surprise collisions
        // and reserved-name bypasses (e.g. "CON." or "CON ").
        result = result.TrimEnd('.', ' ');

        // Windows reserved device names (CON, PRN, AUX, NUL, COM0-9, LPT0-9) cannot be used as
        // file or folder names; prefix them so they remain valid but harmless.
        var baseName = result.Split('.')[0];
        if (IsReservedDeviceName(baseName))
            result = "_" + result;

        return string.IsNullOrWhiteSpace(result) ? "Unknown" : result;
    }

    /// <summary>
    /// True for Windows reserved device names that cannot be used as file or folder names.
    /// </summary>
    private static bool IsReservedDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        if ((name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            name.Length == 4 &&
            name[3] >= '0' && name[3] <= '9')
            return true;

        return false;
    }

    /// <summary>
    /// Resolves a path under the download directory and guarantees it stays inside it. If a
    /// peer-influenced segment would escape (an absolute path or a stray ".." that slipped past
    /// <see cref="Slugify"/>), it logs and falls back to a safe leaf filename inside the directory.
    /// </summary>
    private string ResolveWithinDownloadDirectory(string relativeOrCombinedPath)
    {
        var downloadDir = Path.GetFullPath(_config.DownloadDirectory ?? "Downloads");
        var full = Path.GetFullPath(Path.Combine(downloadDir, relativeOrCombinedPath));

        var prefix = downloadDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var isContained = full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, downloadDir, StringComparison.OrdinalIgnoreCase);

        if (isContained)
            return full;

        _logger.LogWarning("Blocked download path escape: {Path} would leave the download directory {Dir}; using a safe filename instead.", full, downloadDir);

        var leaf = Path.GetFileName(relativeOrCombinedPath);
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..")
            leaf = "download";

        return Path.GetFullPath(Path.Combine(downloadDir, leaf));
    }

    /// <summary>
    /// Scans download directory for orphaned .part files.
    /// Used by "Clean Up Temp Files" feature in Settings.
    /// </summary>
    /// <returns>List of full paths to .part files</returns>
    public List<string> FindOrphanedPartFiles()
    {
        var downloadDir = _config.DownloadDirectory ?? "Downloads";
        if (!Directory.Exists(downloadDir))
        {
            _logger.LogWarning("Download directory does not exist: {Dir}", downloadDir);
            return new List<string>();
        }

        try
        {
            var partFiles = Directory.GetFiles(downloadDir, "*.part", SearchOption.AllDirectories).ToList();
            _logger.LogInformation("Found {Count} .part files in download directory", partFiles.Count);
            return partFiles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan for .part files");
            return new List<string>();
        }
    }

    /// <summary>
    /// Gets the directory path for an album (without creating it).
    /// Used for checking if an album folder already exists.
    /// </summary>
    public string GetAlbumDirectoryPath(string artist, string album)
    {
        var safeArtist = Slugify(artist);
        var safeAlbum = Slugify(album);

        return ResolveWithinDownloadDirectory(Path.Combine(safeArtist, safeAlbum));
    }

    /// <summary>
    /// Phase 2.5: Deletes .part files older than 24 hours that have no corresponding track in Pending state.
    /// Prevents disk bloat from deleted or failed downloads.
    /// </summary>
    /// <param name="activePartPaths">Set of .part file paths currently in use (case-insensitive on Windows)</param>
    /// <returns>Number of orphaned files deleted</returns>
    public async Task<int> CleanupOrphanedPartFilesAsync(HashSet<string> activePartPaths)
    {
        var downloadDir = _config.DownloadDirectory ?? "Downloads";
        if (!Directory.Exists(downloadDir))
        {
            _logger.LogDebug("Download directory does not exist, skipping cleanup: {Dir}", downloadDir);
            return 0;
        }

        var cutoffTime = DateTime.Now.AddHours(-24);
        var deletedCount = 0;
        
        try
        {
            var allPartFiles = Directory.GetFiles(downloadDir, "*.part", SearchOption.AllDirectories);
            
            foreach (var partFile in allPartFiles)
            {
                var fileInfo = new FileInfo(partFile);
                
                // Skip if recently modified OR if it's actively being used
                if (fileInfo.LastWriteTime > cutoffTime || activePartPaths.Contains(partFile))
                    continue;
                
                try
                {
                    File.Delete(partFile);
                    deletedCount++;
                    _logger.LogInformation("🗑️ Deleted orphaned .part file: {File} (age: {Age:F1} hours)", 
                        Path.GetFileName(partFile), 
                        (DateTime.Now - fileInfo.LastWriteTime).TotalHours);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete orphaned .part file: {File}", partFile);
                }
            }
            
            if (deletedCount > 0)
            {
                _logger.LogInformation("✅ Cleanup complete: Deleted {Count} orphaned .part files", deletedCount);
            }
            
            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup orphaned .part files");
            return 0;
        }
    }
}
