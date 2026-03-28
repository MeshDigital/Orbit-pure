using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

public class ScanProgress
{
    public int FilesDiscovered { get; set; }
    public int FilesImported { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesDuplicateByPath { get; set; }
    public int FilesDuplicateByHash { get; set; }
    public int FilesMetadataFailed { get; set; }
    public int FilesAutoUpgraded { get; set; }
    public int FilesMarkedForRemoval { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string CurrentFolder { get; set; } = string.Empty;
}

public class ScanResult
{
    public Guid FolderId { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public int TotalFilesFound { get; set; }
    public int FilesImported { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesDuplicateByPath { get; set; }
    public int FilesDuplicateByHash { get; set; }
    public int FilesMetadataFailed { get; set; }
    public int FilesAutoUpgraded { get; set; }
    public int FilesMarkedForRemoval { get; set; }
    public List<Guid> ImportedLibraryEntryIds { get; set; } = new();
}

internal sealed class ExistingEntryInfo
{
    public string UniqueHash { get; init; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public int Bitrate { get; set; }
    public int QualityScore { get; set; }
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
            result.FolderId = folder.Id;
            result.FolderPath = folder.FolderPath;
            result.TotalFilesFound = audioFiles.Count;
            scanProgress.FilesDiscovered = audioFiles.Count;
            scanProgress.CurrentFolder = folder.FolderPath;
            progress?.Report(scanProgress);

            if (audioFiles.Count == 0) return result;

            // 2. Load existing file paths into memory for O(1) duplicate checking
            // This prevents N+1 DB queries and write starvation checking
            _logger.LogInformation("Loading existing library paths...");
            HashSet<string> existingPaths;
            Dictionary<string, ExistingEntryInfo> existingByHash;
            var pathComparer = GetPathComparer();
            
            // Use a separate short-lived context for reading
            using (var readContext = new AppDbContext())
            {
                // Read only what we need for duplicate detection and alignment.
                var existingEntries = await readContext.LibraryEntries
                    .AsNoTracking()
                    .Select(e => new { e.FilePath, e.UniqueHash, e.Format, e.Bitrate })
                    .ToListAsync(ct);

                existingPaths = existingEntries
                    .Select(e => NormalizePath(e.FilePath))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToHashSet(pathComparer);

                existingByHash = existingEntries
                    .Where(e => !string.IsNullOrWhiteSpace(e.UniqueHash))
                    .GroupBy(e => e.UniqueHash, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToDictionary(
                        e => e.UniqueHash,
                        e => new ExistingEntryInfo
                        {
                            UniqueHash = e.UniqueHash,
                            FilePath = e.FilePath,
                            Format = e.Format ?? string.Empty,
                            Bitrate = e.Bitrate,
                            QualityScore = ComputeQualityScore(e.Format, e.Bitrate, 0, 0)
                        },
                        StringComparer.OrdinalIgnoreCase);
            }
            
            _logger.LogInformation("Loaded {PathCount} existing paths and {HashCount} existing hashes. Starting import...", existingPaths.Count, existingByHash.Count);

            // 3. Process files in batches
            var batch = new List<LibraryEntryEntity>();
            const int BatchSize = 50;

            foreach (var filePath in audioFiles)
            {
                if (ct.IsCancellationRequested) break;
                
                scanProgress.CurrentFile = Path.GetFileName(filePath);
                var normalizedFilePath = NormalizePath(filePath);
                
                // Fast in-memory checks. Path handles exact file dedupe; hash aligns with global library identity.
                if (existingPaths.Contains(normalizedFilePath))
                {
                    result.FilesSkipped++;
                    result.FilesDuplicateByPath++;
                    scanProgress.FilesSkipped++;
                    scanProgress.FilesDuplicateByPath++;
                    progress?.Report(scanProgress);
                    continue;
                }

                // Extract metadata (CPU bound)
                var entry = CreateLibraryEntry(filePath);
                if (entry != null)
                {
                    if (existingByHash.TryGetValue(entry.UniqueHash, out var existing))
                    {
                        result.FilesDuplicateByHash++;
                        scanProgress.FilesDuplicateByHash++;

                        var candidateScore = TryExtractScore(entry.QualityDetails) ?? ComputeQualityScore(entry.Format, entry.Bitrate, 0, 0);
                        if (candidateScore > existing.QualityScore && await TryUpgradeExistingEntryAsync(existing, entry, candidateScore, ct))
                        {
                            result.FilesAutoUpgraded++;
                            result.FilesMarkedForRemoval++;
                            scanProgress.FilesAutoUpgraded++;
                            scanProgress.FilesMarkedForRemoval++;

                            existing.FilePath = entry.FilePath;
                            existing.Format = entry.Format;
                            existing.Bitrate = entry.Bitrate;
                            existing.QualityScore = candidateScore;
                            existingPaths.Add(normalizedFilePath);

                            progress?.Report(scanProgress);
                            continue;
                        }

                        result.FilesSkipped++;
                        scanProgress.FilesSkipped++;
                        progress?.Report(scanProgress);
                        continue;
                    }

                    batch.Add(entry);
                    result.ImportedLibraryEntryIds.Add(entry.Id);
                    existingPaths.Add(normalizedFilePath);
                    existingByHash[entry.UniqueHash] = new ExistingEntryInfo
                    {
                        UniqueHash = entry.UniqueHash,
                        FilePath = entry.FilePath,
                        Format = entry.Format,
                        Bitrate = entry.Bitrate,
                        QualityScore = TryExtractScore(entry.QualityDetails) ?? ComputeQualityScore(entry.Format, entry.Bitrate, 0, 0)
                    };
                }
                else
                {
                    result.FilesSkipped++;
                    result.FilesMetadataFailed++;
                    scanProgress.FilesSkipped++;
                    scanProgress.FilesMetadataFailed++;
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

            _logger.LogInformation(
                "✅ Scan complete: {Imported} imported, {Skipped} skipped (dup-path: {DupPath}, dup-hash: {DupHash}, upgraded: {Upgraded}, removal-candidates: {RemovalCandidates}, metadata-failed: {MetaFailed})",
                result.FilesImported,
                result.FilesSkipped,
                result.FilesDuplicateByPath,
                result.FilesDuplicateByHash,
                result.FilesAutoUpgraded,
                result.FilesMarkedForRemoval,
                result.FilesMetadataFailed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning folder {Path}", folder.FolderPath);
        }

        return result;
    }

    /// <summary>
    /// Phase 12: Delta Scan Optimization - Fast sync using file system timestamps
    /// Only scans folders that have changed since the last scan
    /// </summary>
    public async Task<ScanResult> FastSyncLibraryAsync(IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("🔄 Starting fast library sync (delta scan)...");

        var result = new ScanResult();
        var scanProgress = new ScanProgress();

        using var context = new AppDbContext();

        // Get all enabled library folders
        var folders = await context.LibraryFolders
            .Where(f => f.IsEnabled)
            .ToListAsync(ct);

        var foldersToScan = new List<LibraryFolderEntity>();

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder.FolderPath))
            {
                _logger.LogWarning("Folder no longer exists: {Path}", folder.FolderPath);
                continue;
            }

            // Check if folder has changed since last scan
            var folderInfo = new DirectoryInfo(folder.FolderPath);
            var lastWriteTime = folderInfo.LastWriteTimeUtc;

            // If never scanned or folder has been modified, add to scan list
            if (!folder.LastScannedAt.HasValue || lastWriteTime > folder.LastScannedAt.Value)
            {
                foldersToScan.Add(folder);
                _logger.LogInformation("📁 Folder changed, will scan: {Path} (last modified: {LastWrite})",
                    folder.FolderPath, lastWriteTime);
            }
            else
            {
                _logger.LogDebug("📁 Folder unchanged, skipping: {Path}", folder.FolderPath);
            }
        }

        if (!foldersToScan.Any())
        {
            _logger.LogInformation("✅ Fast sync complete: No folders changed since last scan");
            return result;
        }

        _logger.LogInformation("🔍 Fast sync will scan {Count} of {Total} folders", foldersToScan.Count, folders.Count);

        // Scan the changed folders
        foreach (var folder in foldersToScan)
        {
            if (ct.IsCancellationRequested) break;

            var folderResult = await ScanFolderAsync(folder.Id, progress, ct);
            result.TotalFilesFound += folderResult.TotalFilesFound;
            result.FilesImported += folderResult.FilesImported;
            result.FilesSkipped += folderResult.FilesSkipped;
            result.FilesDuplicateByPath += folderResult.FilesDuplicateByPath;
            result.FilesDuplicateByHash += folderResult.FilesDuplicateByHash;
            result.FilesAutoUpgraded += folderResult.FilesAutoUpgraded;
            result.FilesMarkedForRemoval += folderResult.FilesMarkedForRemoval;
            result.FilesMetadataFailed += folderResult.FilesMetadataFailed;
            result.ImportedLibraryEntryIds.AddRange(folderResult.ImportedLibraryEntryIds);
        }

        _logger.LogInformation(
            "✅ Fast sync complete: {Imported} imported, {Skipped} skipped from {Folders} folders (dup-path: {DupPath}, dup-hash: {DupHash}, upgraded: {Upgraded}, removal-candidates: {RemovalCandidates}, metadata-failed: {MetaFailed})",
            result.FilesImported,
            result.FilesSkipped,
            foldersToScan.Count,
            result.FilesDuplicateByPath,
            result.FilesDuplicateByHash,
            result.FilesAutoUpgraded,
            result.FilesMarkedForRemoval,
            result.FilesMetadataFailed);

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
            int bitrate = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;

            try
            {
                var file = TagLib.File.Create(filePath);
                artist = string.IsNullOrWhiteSpace(file.Tag.FirstPerformer) ? artist : file.Tag.FirstPerformer;
                title = string.IsNullOrWhiteSpace(file.Tag.Title) ? title : file.Tag.Title;
                album = file.Tag.Album ?? string.Empty;
                duration = (int)file.Properties.Duration.TotalSeconds;
                bitrate = file.Properties.AudioBitrate;
                sampleRate = file.Properties.AudioSampleRate;
                bitsPerSample = file.Properties.BitsPerSample;
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

            var format = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            var qualityScore = ComputeQualityScore(format, bitrate, sampleRate, bitsPerSample);

            return new LibraryEntryEntity
            {
                Id = Guid.NewGuid(),
                UniqueHash = BuildLibraryUniqueHash(artist, title, duration, filePath),
                Artist = artist,
                Title = title,
                Album = album,
                DurationSeconds = duration,
                FilePath = filePath,
                AddedAt = DateTime.UtcNow,
                Bitrate = bitrate,
                Format = format,
                QualityDetails = $"scanner:v2;score={qualityScore};format={format};bitrate={bitrate};samplerate={sampleRate};bitdepth={bitsPerSample}"
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
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.System
                };

                var allFiles = Directory.EnumerateFiles(folderPath, "*.*", options);
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

        var normalizedLower = normalizedPath.ToLowerInvariant();

        var exists = await context.LibraryFolders
            .AnyAsync(f => f.FolderPath.ToLower() == normalizedLower);
            
        if (!exists)
        {
            // Double check with more complex logic if needed, but for now simple add
            _logger.LogInformation("Registering new default library folder: {Path}", path);
            
            context.LibraryFolders.Add(new LibraryFolderEntity 
            { 
                FolderPath = normalizedPath,
                IsEnabled = true,
                AddedAt = DateTime.UtcNow
            });
            
            await context.SaveChangesAsync();
        }
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string BuildLibraryUniqueHash(string artist, string title, int durationSeconds, string filePath)
    {
        static string NormalizeToken(string value)
        {
            return new string((value ?? string.Empty)
                .ToLowerInvariant()
                .Where(c => !char.IsWhiteSpace(c))
                .ToArray());
        }

        var artistToken = NormalizeToken(artist);
        var titleToken = NormalizeToken(title);

        // Align with Track.UniqueHash identity format where possible.
        if (!string.IsNullOrWhiteSpace(artistToken) || !string.IsNullOrWhiteSpace(titleToken))
        {
            return $"{artistToken}-{titleToken}".Trim('-');
        }

        // Fallback for tag-less files: stable hash from path + duration.
        var canonical = $"{NormalizePath(filePath).ToLowerInvariant()}|{durationSeconds}";
        using var sha1 = SHA1.Create();
        var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int ComputeQualityScore(string? format, int bitrate, int sampleRate, int bitsPerSample)
    {
        var fmt = (format ?? string.Empty).Trim().ToLowerInvariant();

        int score = fmt switch
        {
            "flac" => 500,
            "wav" => 470,
            "aiff" => 460,
            "aif" => 460,
            "m4a" => 260,
            "aac" => 250,
            "ogg" => 240,
            "mp3" => 200,
            _ => 150
        };

        score += Math.Clamp(bitrate, 0, 1600) / 4;

        if (sampleRate >= 96000) score += 120;
        else if (sampleRate >= 48000) score += 40;

        if (bitsPerSample >= 24) score += 80;

        return score;
    }

    private static int? TryExtractScore(string? qualityDetails)
    {
        if (string.IsNullOrWhiteSpace(qualityDetails)) return null;

        const string marker = "score=";
        var idx = qualityDetails.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var start = idx + marker.Length;
        var end = qualityDetails.IndexOf(';', start);
        var token = end >= 0 ? qualityDetails[start..end] : qualityDetails[start..];

        return int.TryParse(token, out var value) ? value : null;
    }

    private async Task<bool> TryUpgradeExistingEntryAsync(ExistingEntryInfo existing, LibraryEntryEntity candidate, int candidateScore, CancellationToken ct)
    {
        try
        {
            using var context = new AppDbContext();
            var entry = await context.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == candidate.UniqueHash, ct);
            if (entry == null) return false;

            var currentPath = NormalizePath(entry.FilePath);
            var candidatePath = NormalizePath(candidate.FilePath);

            if (GetPathComparer().Equals(currentPath, candidatePath)) return false;

            var previousPath = entry.FilePath;

            entry.FilePath = candidate.FilePath;
            entry.Format = candidate.Format;
            entry.Bitrate = Math.Max(candidate.Bitrate, entry.Bitrate);
            if (candidate.DurationSeconds.HasValue && candidate.DurationSeconds.Value > 0)
            {
                entry.DurationSeconds = candidate.DurationSeconds;
            }

            if (string.IsNullOrWhiteSpace(entry.OriginalFilePath))
            {
                entry.OriginalFilePath = previousPath;
            }

            entry.FilePathUpdatedAt = DateTime.UtcNow;
            entry.QualityDetails = $"scanner-upgrade:v1;score={candidateScore};from={previousPath};to={candidate.FilePath}";
            entry.Comments = AppendRemovalCandidateComment(entry.Comments, previousPath);

            context.LibraryActionLogs.Add(new LibraryActionLogEntity
            {
                BatchId = Guid.NewGuid(),
                ActionType = LibraryActionType.Consolidate,
                SourcePath = previousPath,
                DestinationPath = candidate.FilePath,
                Timestamp = DateTime.Now,
                TrackArtist = entry.Artist,
                TrackTitle = entry.Title
            });

            await context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Auto-upgraded quality for {Hash}: {From} -> {To} (score {Score})",
                entry.UniqueHash,
                previousPath,
                candidate.FilePath,
                candidateScore);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-upgrade duplicate {Hash}", candidate.UniqueHash);
            return false;
        }
    }

    private static string AppendRemovalCandidateComment(string? existing, string pathToRemove)
    {
        var marker = $"AUTO_REMOVE_CANDIDATE:{pathToRemove}";
        if (!string.IsNullOrWhiteSpace(existing) && existing.Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        return string.IsNullOrWhiteSpace(existing)
            ? marker
            : $"{existing} | {marker}";
    }
}

