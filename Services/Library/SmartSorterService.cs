using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data; // For AppDbContext
using SLSKDONET.Models;
using SLSKDONET.Services.IO;

namespace SLSKDONET.Services.Library
{
    public class SmartSorterService
    {
        private readonly ILogger<SmartSorterService> _logger;
        private readonly IFileWriteService _fileWriteService;
        private readonly AppConfig _config;

        public SmartSorterService(
            ILogger<SmartSorterService> logger,
            IFileWriteService fileWriteService,
            AppConfig config)
        {
            _logger = logger;
            _fileWriteService = fileWriteService;
            _config = config;
        }

        public async Task<List<FileMoveOperation>> PlanSortAsync(List<LibraryEntry> entries)
        {
            var operations = new List<FileMoveOperation>();
            var musicRoot = _config.DownloadDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

            // Phase 16.1: Fetch Vibe Data from DB (AudioFeaturesEntity)
            // LibraryEntry does not hold the Vibe data directly
            var hashes = entries.Select(e => e.UniqueHash).ToList();
            Dictionary<string, Data.Entities.AudioFeaturesEntity> featureMap;

            using (var context = new AppDbContext())
            {
                // Chunk the query if needed, but for now select all matching
                // Assuming AudioFeaturesEntity has TrackUniqueHash as key
                var features = await context.AudioFeatures
                    .Where(af => hashes.Contains(af.TrackUniqueHash))
                    .ToListAsync();
                    
                featureMap = features.ToDictionary(f => f.TrackUniqueHash);
            }

            await Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    // 1. Check for Audio Features & Vibe
                    if (!featureMap.TryGetValue(entry.UniqueHash, out var features)) continue;

                    var vibe = features.PredictedVibe;
                    var confidence = features.PredictionConfidence;

                    // Skip if no confidence or no predicted vibe
                    if (string.IsNullOrEmpty(vibe) || confidence < 0.8f)
                    {
                         continue;
                    }

                    // 2. Generate New Path
                    // Structure: {Root}/{Vibe}/{Artist} - {Title}.ext
                    var artist = Sanitize(entry.Artist ?? "Unknown Artist");
                    var title = Sanitize(entry.Title ?? "Unknown Track");
                    var ext = Path.GetExtension(entry.FilePath) ?? ".mp3";
                    
                    var vibeFolder = Sanitize(vibe);
                    var fileName = $"{artist} - {title}{ext}";

                    var destPath = Path.Combine(musicRoot, vibeFolder, fileName);
                    
                    // Skip if already in place
                    if (string.Equals(entry.FilePath, destPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 3. Collision Strategy (Proactive check)
                    var isCollision = File.Exists(destPath);
                    if (isCollision)
                    {
                        // Strategy: Append (1), (2)
                        destPath = GetUniquePath(destPath);
                    }

                    operations.Add(new FileMoveOperation
                    {
                        SourcePath = entry.FilePath,
                        DestinationPath = destPath,
                        Artist = entry.Artist ?? "",
                        TrackTitle = entry.Title ?? "",
                        PredictedVibe = vibe,
                        Confidence = confidence,
                        IsCollisionExpected = isCollision,
                        Status = "Pending"
                    });
                }
            });

            return operations;
        }

        public async Task ExecuteSortAsync(List<FileMoveOperation> ops, Action<string>? progressCallback = null)
        {
            var batchId = Guid.NewGuid(); // Phase 16.1: Ledger Batch ID
            
            // We iterate one by one to ensure atomicity per file
            foreach (var op in ops)
            {
                if (!op.IsChecked) continue;

                progressCallback?.Invoke($"Moving: {op.DisplayName}");
                op.Status = "Moving...";

                // Skip if source missing
                if (!File.Exists(op.SourcePath))
                {
                    op.Status = "Skipped (Source Missing)";
                    op.ErrorMessage = "Source file not found";
                    continue;
                }

                // Skip if destination exists (double check race condition)
                if (File.Exists(op.DestinationPath))
                {
                    // Fallback to rename just in case proactive check failed or race condition
                    op.DestinationPath = GetUniquePath(op.DestinationPath);
                }

                bool moveSuccess = false;
                
                // ATOMIC TRANSACTION: File + DB
                // Use a fresh context for the transaction to ensure clean state
                using var context = new AppDbContext();
                using (var transaction = await context.Database.BeginTransactionAsync())
                {
                    try
                    {
                        // 1. Physical Move (Using SafeWriteService)
                        // Note: SafeWriteService.MoveAtomicAsync does Copy+Verify+Delete
                        moveSuccess = await _fileWriteService.MoveAtomicAsync(op.SourcePath, op.DestinationPath);
                        
                        if (!moveSuccess)
                        {
                            throw new IOException($"Atomic move failed for {op.SourcePath}");
                        }

                        // 2. Update Database (Atomic Transaction)
                        
                        // A. Update LibraryEntry (Primary Source)
                        var libEntry = await context.LibraryEntries.FirstOrDefaultAsync(e => e.FilePath == op.SourcePath);
                        if (libEntry != null)
                        {
                            libEntry.FilePath = op.DestinationPath;
                            libEntry.FilePathUpdatedAt = DateTime.UtcNow;
                        }

                        // B. Update TrackEntity (Queue Persistence) - Use 'Filename'
                        var track = await context.Tracks.FirstOrDefaultAsync(t => t.Filename == op.SourcePath);
                        if (track != null)
                        {
                            track.Filename = op.DestinationPath;
                        }
                        
                        // C. Update PlaylistTracks (Project/Playlist references) - Use 'ResolvedFilePath'
                        var playlistTracks = await context.PlaylistTracks.Where(pt => pt.ResolvedFilePath == op.SourcePath).ToListAsync();
                        foreach (var pt in playlistTracks)
                        {
                            pt.ResolvedFilePath = op.DestinationPath;
                        }

                        // Phase 16.1: Ledger - Log the action
                        // Use metadata from LibraryEntry if available, else fallback to Op
                        context.LibraryActionLogs.Add(new Data.Entities.LibraryActionLogEntity
                        {
                            BatchId = batchId,
                            ActionType = Data.Entities.LibraryActionType.SmartSort,
                            SourcePath = op.SourcePath,
                            DestinationPath = op.DestinationPath,
                            TrackArtist = libEntry?.Artist ?? op.Artist,
                            TrackTitle = libEntry?.Title ?? op.TrackTitle,
                            Timestamp = DateTime.Now
                        });
                        
                        await context.SaveChangesAsync();

                        // 3. Commit
                        await transaction.CommitAsync();
                        op.Status = "Success";
                        _logger.LogInformation("Sorted: {Source} -> {Dest}", op.SourcePath, op.DestinationPath);
                    }
                    catch (Exception ex)
                    {
                        // ROLLBACK logic
                        op.Status = "Failed";
                        op.ErrorMessage = ex.Message;
                        _logger.LogError(ex, "Sort failed for {Path}", op.SourcePath);

                        // If file was moved but DB failed/broke
                        if (moveSuccess && File.Exists(op.DestinationPath))
                        {
                             try
                             {
                                 // Compulsive Rollback: Move file back
                                 _logger.LogInformation("Rolling back file move: {Dest} -> {Source}", op.DestinationPath, op.SourcePath);
                                 File.Move(op.DestinationPath, op.SourcePath); 
                                 // We use simple File.Move for rollback as SafeWrite might be overkill/slow during emergency rollback
                             }
                             catch (Exception rollbackEx)
                             {
                                 _logger.LogCritical(rollbackEx, "CRITICAL: Rollback failed! File stranded at {Dest}", op.DestinationPath);
                                 op.ErrorMessage += " (Rollback Failed)";
                             }
                        }
                        
                        // Transaction auto-rolls back on disposal if not committed
                    }
                }
            }
        }

        private string Sanitize(string input)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", input.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private string GetUniquePath(string path)
        {
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            
            int i = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(dir!, $"{name} ({i}){ext}");
                i++;
            }
            return path;
        }
    }
}
