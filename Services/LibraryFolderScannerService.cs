using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

public class ScanProgress
{
    public int FilesDiscovered { get; set; }
    public int FilesImported { get; set; }
    public int FilesSkipped { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
}

public class ScanResult
{
    public int TotalFilesFound { get; set; }
    public int FilesImported { get; set; }
    public int FilesSkipped { get; set; }
    public List<Guid> ImportedLibraryEntryIds { get; set; } = new();
}

public class LibraryFolderScannerService
{
    private readonly ILogger<LibraryFolderScannerService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly LibraryService _libraryService;
    
    private static readonly string[] SupportedExtensions = new[] { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg" };

    public LibraryFolderScannerService(
        ILogger<LibraryFolderScannerService> logger,
        DatabaseService databaseService,
        LibraryService libraryService)
    {
        _logger = logger;
        _databaseService = databaseService;
        _libraryService = libraryService;
    }

    /// <summary>
    /// Scans a specific library folder and imports new audio files
    /// </summary>
    public async Task<ScanResult> ScanFolderAsync(Guid folderId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        using var metaContext = new AppDbContext();
        var folder = await metaContext.LibraryFolders.FindAsync(new object[] { folderId }, ct);
        
        if (folder == null || !folder.IsEnabled) return new ScanResult();
        if (!Directory.Exists(folder.FolderPath)) return new ScanResult();

        _logger.LogInformation("📁 Scanning folder: {Path}", folder.FolderPath);
        
        var result = new ScanResult();
        var scanProgress = new ScanProgress();
        
        try
        {
            // 1. Discover all audio files (CPU bound)
            var audioFiles = await DiscoverAudioFilesAsync(folder.FolderPath, ct);
            result.TotalFilesFound = audioFiles.Count;
            scanProgress.FilesDiscovered = audioFiles.Count;
            progress?.Report(scanProgress);

            if (audioFiles.Count == 0) return result;

            // 2. Load existing file paths into memory for O(1) duplicate checking
            // This prevents N+1 DB queries and write starvation checking
            _logger.LogInformation("Loading existing library paths...");
            HashSet<string> existingPaths;
            
            // Use a separate short-lived context for reading
            using (var readContext = new AppDbContext())
            {
                // We only need the FilePath string
                existingPaths = await readContext.LibraryEntries
                    .AsNoTracking()
                    .Select(e => e.FilePath)
                    .ToHashSetAsync(ct);
            }
            
            _logger.LogInformation("Loaded {Count} existing paths. Starting import...", existingPaths.Count);

            // 3. Process files in batches
            var batch = new List<LibraryEntryEntity>();
            const int BatchSize = 50;

            foreach (var filePath in audioFiles)
            {
                if (ct.IsCancellationRequested) break;
                
                scanProgress.CurrentFile = Path.GetFileName(filePath);
                
                // Fast in-memory check
                if (existingPaths.Contains(filePath))
                {
                    result.FilesSkipped++;
                    scanProgress.FilesSkipped++;
                    progress?.Report(scanProgress);
                    continue;
                }

                // Extract metadata (CPU bound)
                var entry = CreateLibraryEntry(filePath);
                if (entry != null)
                {
                    batch.Add(entry);
                    result.ImportedLibraryEntryIds.Add(entry.Id);
                }
                else
                {
                    result.FilesSkipped++;
                    scanProgress.FilesSkipped++;
                }

                // Flush batch if full
                if (batch.Count >= BatchSize)
                {
                    await SaveBatchAsync(batch, ct);
                    
                    result.FilesImported += batch.Count;
                    scanProgress.FilesImported += batch.Count;
                    progress?.Report(scanProgress);
                    
                    batch.Clear();
                    
                    // CRITICAL: Yield control to UI and other services (prevent Write Starvation)
                    await Task.Yield();
                }
            }

            // Flush remaining
            if (batch.Count > 0)
            {
                await SaveBatchAsync(batch, ct);
                result.FilesImported += batch.Count;
                scanProgress.FilesImported += batch.Count;
                progress?.Report(scanProgress);
            }

            // Update folder metadata
            folder.LastScannedAt = DateTime.UtcNow;
            folder.TracksFound = result.TotalFilesFound;
            await metaContext.SaveChangesAsync(ct);

            _logger.LogInformation("✅ Scan complete: {Imported} imported, {Skipped} skipped", 
                result.FilesImported, result.FilesSkipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning folder {Path}", folder.FolderPath);
        }

        return result;
    }

    /// <summary>
    /// Saves a batch of entries to the database in a single transaction
    /// </summary>
    private async Task SaveBatchAsync(List<LibraryEntryEntity> batch, CancellationToken ct)
    {
        try 
        {
            using var context = new AppDbContext();
            context.LibraryEntries.AddRange(batch);
            await context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save batch of {Count} entries", batch.Count);
        }
    }

    /// <summary>
    /// Creates a LibraryEntryEntity from a file path using TagLib
    /// </summary>
    private LibraryEntryEntity? CreateLibraryEntry(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            string artist = "Unknown Artist";
            string title = Path.GetFileNameWithoutExtension(filePath);
            string album = string.Empty;
            int duration = 0;

            try
            {
                var file = TagLib.File.Create(filePath);
                artist = string.IsNullOrWhiteSpace(file.Tag.FirstPerformer) ? artist : file.Tag.FirstPerformer;
                title = string.IsNullOrWhiteSpace(file.Tag.Title) ? title : file.Tag.Title;
                album = file.Tag.Album ?? string.Empty;
                duration = (int)file.Properties.Duration.TotalSeconds;
            }
            catch
            {
                // Tags failed - use smart filename parsing
                var parsed = ParseFilename(title);
                artist = parsed.Artist ?? artist;
                title = parsed.Title ?? title;
                
                _logger.LogDebug("Parsed filename '{File}' as Artist='{A}', Title='{T}'", 
                    Path.GetFileName(filePath), artist, title);
            }

            return new LibraryEntryEntity
            {
                Id = Guid.NewGuid(),
                UniqueHash = Guid.NewGuid().ToString(), // TODO: Calculate real hash if needed
                Artist = artist,
                Title = title,
                Album = album,
                DurationSeconds = duration,
                FilePath = filePath,
                AddedAt = DateTime.UtcNow,
                Bitrate = 0, 
                Format = Path.GetExtension(filePath).TrimStart('.')
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create entry for {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Parses a filename to extract artist and title, handling common patterns:
    /// - "01. Artist - Title"
    /// - "Artist - Title"
    /// - "01 Artist - Title"
    /// - "Title" (no artist)
    /// </summary>
    private (string? Artist, string? Title) ParseFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return (null, null);

        // Remove common track number prefixes: "01. ", "01 ", "1. ", "1 "
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            filename, 
            @"^\d{1,3}[\.\s]+", 
            ""
        ).Trim();

        // Try to split on " - " for "Artist - Title" format
        if (cleaned.Contains(" - "))
        {
            var parts = cleaned.Split(new[] { " - " }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                return (parts[0].Trim(), parts[1].Trim());
            }
        }

        // No artist found, return just the title
        return (null, cleaned);
    }

    /// <summary>
    /// Recursively discovers all audio files in a folder
    /// </summary>
    private async Task<List<string>> DiscoverAudioFilesAsync(string folderPath, CancellationToken ct)
    {
        var audioFiles = new List<string>();
        
        await Task.Run(() =>
        {
            try
            {
                var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
                audioFiles.AddRange(allFiles.Where(f => 
                    SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error discovering files in {Path}: {Message}", folderPath, ex.Message);
            }
        }, ct);

        return audioFiles;
    }

    /// <summary>
    /// Scans all enabled library folders
    /// </summary>
    public async Task<Dictionary<Guid, ScanResult>> ScanAllFoldersAsync(IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        using var context = new AppDbContext();
        var folders = await context.LibraryFolders
            .Where(f => f.IsEnabled)
            .ToListAsync(ct);

        _logger.LogInformation("Scanning {Count} enabled library folders", folders.Count);

        var results = new Dictionary<Guid, ScanResult>();

        foreach (var folder in folders)
        {
            if (ct.IsCancellationRequested) break;
            
            var result = await ScanFolderAsync(folder.Id, progress, ct);
            results[folder.Id] = result;
        }

        return results;
    }
    /// <summary>
    /// Ensures that the specified path is registered as a library folder.
    /// Uses the existing folder if found, or creates a new one.
    /// </summary>
    public async Task EnsureDefaultFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        using var context = new AppDbContext();
        
        // Check if already exists (case-insensitive for Windows)
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        
        var exists = await context.LibraryFolders
            .AnyAsync(f => f.FolderPath == path); // Simple check first
            
        if (!exists)
        {
            // Double check with more complex logic if needed, but for now simple add
            _logger.LogInformation("Registering new default library folder: {Path}", path);
            
            context.LibraryFolders.Add(new LibraryFolderEntity 
            { 
                FolderPath = path,
                IsEnabled = true,
                AddedAt = DateTime.UtcNow
            });
            
            await context.SaveChangesAsync();
        }
    }
}

