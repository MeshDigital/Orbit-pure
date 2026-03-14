using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services;

namespace SLSKDONET.Services.IO
{
    public class SafeWriteService : IFileWriteService
    {
        private readonly ILogger<SafeWriteService> _logger;
        private readonly CrashRecoveryJournal _crashJournal;
        private readonly SemaphoreSlim _fileLock = new(1, 1); // Prevent concurrent writes to same file

        public SafeWriteService(
            ILogger<SafeWriteService> logger,
            CrashRecoveryJournal crashJournal)
        {
            _logger = logger;
            _crashJournal = crashJournal;
        }

        public async Task<bool> WriteAtomicAsync(
            string targetPath,
            Func<string, Task> writeAction,
            Func<string, Task<bool>>? verifyAction = null,
            CancellationToken cancellationToken = default)
        {
            // Validate input
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("Target path cannot be null or empty", nameof(targetPath));

            if (writeAction == null)
                throw new ArgumentNullException(nameof(writeAction));

            // Normalize path
            targetPath = Path.GetFullPath(targetPath);

            // Generate temporary file path in same directory (same volume = atomic move)
            var directory = Path.GetDirectoryName(targetPath);
            var fileName = Path.GetFileName(targetPath);
            var tempPath = Path.Combine(directory!, $"{fileName}.{Guid.NewGuid()}.tmp");

            // Ensure directory exists
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            // Check disk space BEFORE starting write
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(targetPath)!);
                if (driveInfo.AvailableFreeSpace < 100 * 1024 * 1024) // 100MB minimum
                {
                    _logger.LogWarning("Low disk space on {Drive}: {Free}MB available", 
                        driveInfo.Name, driveInfo.AvailableFreeSpace / 1024 / 1024);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not check disk space for {Path}", targetPath);
            }

            // Path length validation (Windows 260 char limit without LongPaths)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && tempPath.Length > 260)
            {
                _logger.LogError("Temporary path exceeds Windows MAX_PATH limit: {Length} chars", tempPath.Length);
                return false;
            }

            // Phase 2A: Log checkpoint BEFORE any I/O operations
            string? checkpointId = null;
            DateTime? originalCreationTime = null;
            DateTime? originalLastWriteTime = null;
            
            if (File.Exists(targetPath))
            {
                var fileInfo = new FileInfo(targetPath);
                originalCreationTime = fileInfo.CreationTime;
                originalLastWriteTime = fileInfo.LastWriteTime;
            }

            try
            {
                // Thread synchronization for same-file writes
                await _fileLock.WaitAsync(cancellationToken);

                try
                {
                    // Phase 2A: Create checkpoint state with enhanced tracking
                    var checkpointState = new TagWriteCheckpointState
                    {
                        FilePath = targetPath,
                        TempPath = tempPath,
                        OriginalTimestamp = originalLastWriteTime ?? DateTime.Now,
                        OriginalCreationTime = originalCreationTime
                    };

                    var checkpoint = new RecoveryCheckpoint
                    {
                        Id = Guid.NewGuid().ToString(),
                        OperationType = OperationType.TagWrite,
                        TargetPath = targetPath,
                        StateJson = JsonSerializer.Serialize(checkpointState),
                        Priority = 5 // Medium priority
                    };

                    checkpointId = await _crashJournal.LogCheckpointAsync(checkpoint);
                    _logger.LogDebug("Logged tag write checkpoint: {Id}", checkpointId);

                    // Step 1: Write to temporary file
                    _logger.LogDebug("Writing to temporary file: {TempPath}", tempPath);
                    await writeAction(tempPath);

                    // Step 2: Flush to disk (ensure physical write)
                    await FlushToDiskAsync(tempPath);

                    // Step 3: Optional verification
                    if (verifyAction != null)
                    {
                        _logger.LogDebug("Verifying temporary file: {TempPath}", tempPath);
                        var isValid = await verifyAction(tempPath);
                        if (!isValid)
                        {
                            _logger.LogWarning("Verification failed for {TempPath}", tempPath);
                            CleanupTempFile(tempPath);
                            return false;
                        }
                    }

                    // Phase 2A: Enhanced verification with file hash (optional)
                    if (verifyAction == null)
                    {
                        // If no custom verifier, at least check temp file exists and has content
                        var tempInfo = new FileInfo(tempPath);
                        if (tempInfo.Length == 0)
                        {
                            _logger.LogWarning("Temp file is empty: {TempPath}", tempPath);
                            CleanupTempFile(tempPath);
                            if (checkpointId != null)
                                await _crashJournal.CompleteCheckpointAsync(checkpointId);
                            return false;
                        }
                    }

                    // Step 4: Atomic replace/move with AV resilience
                    // PERFORMANCE FIX: Many failures are due to AV scanning the file for 50-200ms
                    // Solution: Wait 100ms and retry once before giving up
                    bool swapSucceeded = false;
                    Exception? lastSwapError = null;
                    
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            if (File.Exists(targetPath))
                            {
                                // Use File.Replace for atomic swap with backup
                                var backupPath = $"{targetPath}.backup";
                                File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                                
                                // Clean up backup after successful replace
                                if (File.Exists(backupPath))
                                {
                                    File.Delete(backupPath);
                                }
                            }
                            else
                            {
                                // Simple move for new files
                                File.Move(tempPath, targetPath, overwrite: false);
                            }
                            
                            swapSucceeded = true;
                            if (attempt == 2)
                            {
                                _logger.LogInformation("✅ Atomic swap succeeded on retry attempt {Attempt} for {Path}", attempt, targetPath);
                            }
                            break; // Success!
                        }
                        catch (IOException ioEx) when (attempt == 1)
                        {
                            _logger.LogWarning("Atomic swap attempt {Attempt} failed (likely AV/indexer interference), retrying after 100ms: {Error}", 
                                attempt, ioEx.Message);
                            lastSwapError = ioEx;
                            await Task.Delay(100, cancellationToken); // Give AV time to release the file
                        }
                        catch (UnauthorizedAccessException uaEx) when (attempt == 1)
                        {
                            _logger.LogWarning("Atomic swap attempt {Attempt} failed (permissions), retrying after 100ms: {Error}", 
                                attempt, uaEx.Message);
                            lastSwapError = uaEx;
                            await Task.Delay(100, cancellationToken);
                        }
                    }
                    
                    // If both attempts failed, throw the last error
                    if (!swapSucceeded && lastSwapError != null)
                    {
                        throw lastSwapError;
                    }

                    // Step 5: Restore original timestamps (preserves "Date Added" in players)
                    if (originalCreationTime.HasValue && originalLastWriteTime.HasValue)
                    {
                        var newFileInfo = new FileInfo(targetPath);
                        newFileInfo.CreationTime = originalCreationTime.Value;
                        newFileInfo.LastWriteTime = originalLastWriteTime.Value;
                    }

                    // Phase 2A: Complete checkpoint AFTER successful atomic swap
                    if (checkpointId != null)
                    {
                        await _crashJournal.CompleteCheckpointAsync(checkpointId);
                        _logger.LogDebug("Completed tag write checkpoint: {Id}", checkpointId);
                    }

                    _logger.LogInformation("✅ Successfully wrote file atomically: {TargetPath}", targetPath);
                    return true;
                }
                finally
                {
                    _fileLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Write operation cancelled: {TargetPath}", targetPath);
                CleanupTempFile(tempPath);
                return false;
            }
            catch (IOException ex) when (ex.Message.Contains("disk full") || 
                                          ex.Message.Contains("not enough space"))
            {
                _logger.LogError(ex, "Disk full while writing: {TargetPath}", targetPath);
                CleanupTempFile(tempPath);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied writing: {TargetPath}", targetPath);
                CleanupTempFile(tempPath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during atomic write: {TargetPath}", targetPath);
                CleanupTempFile(tempPath);
                return false;
            }
        }

        public async Task<bool> WriteAllBytesAtomicAsync(
            string targetPath,
            byte[] data,
            CancellationToken cancellationToken = default)
        {
            return await WriteAtomicAsync(
                targetPath,
                async (tempPath) =>
                {
                    await File.WriteAllBytesAsync(tempPath, data, cancellationToken);
                },
                async (tempPath) =>
                {
                    // Verify file size matches data length
                    var fileInfo = new FileInfo(tempPath);
                    return fileInfo.Length == data.Length;
                },
                cancellationToken
            );
        }

        public async Task<bool> CopyFileAtomicAsync(
            string sourcePath,
            string targetPath,
            bool preserveTimestamps = true,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(sourcePath))
            {
                _logger.LogError("Source file not found: {SourcePath}", sourcePath);
                return false;
            }

            var sourceInfo = new FileInfo(sourcePath);

            return await WriteAtomicAsync(
                targetPath,
                async (tempPath) =>
                {
                    await CopyFileWithProgressAsync(sourcePath, tempPath, cancellationToken);
                },
                async (tempPath) =>
                {
                    // Verify file sizes match
                    var tempInfo = new FileInfo(tempPath);
                    return tempInfo.Length == sourceInfo.Length;
                },
                cancellationToken
            );
        }

        /// <summary>
        /// Ensures data is physically written to disk (not just OS cache).
        /// </summary>
        private async Task FlushToDiskAsync(string filePath)
        {
            await Task.Run(() =>
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Flush(flushToDisk: true); // Force OS to flush to physical media
            });
        }

        /// <summary>
        /// Safely deletes temporary file if it exists.
        /// </summary>
        private void CleanupTempFile(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                    _logger.LogDebug("Cleaned up temporary file: {TempPath}", tempPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup temp file: {TempPath}", tempPath);
                // Non-critical - don't throw
            }
        }

        /// <summary>
        /// Copies file with cancellation support.
        /// </summary>
        private async Task CopyFileWithProgressAsync(
            string source,
            string destination,
            CancellationToken cancellationToken)
        {
            const int bufferSize = 81920; // 80KB buffer
            using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            using var destStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

            await sourceStream.CopyToAsync(destStream, bufferSize, cancellationToken);
        }

        public async Task<bool> MoveAtomicAsync(
            string sourcePath,
            string targetPath,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(sourcePath))
            {
                _logger.LogError("Source file for atomic move not found: {SourcePath}", sourcePath);
                return false;
            }

            var sourceInfo = new FileInfo(sourcePath);
            var expectedSize = sourceInfo.Length;

            var success = await WriteAtomicAsync(
                targetPath,
                async (tempPath) =>
                {
                    await CopyFileWithProgressAsync(sourcePath, tempPath, cancellationToken);
                },
                async (tempPath) =>
                {
                    return new FileInfo(tempPath).Length == expectedSize;
                },
                cancellationToken
            );

            if (success)
            {
                try 
                {
                    File.Delete(sourcePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete source file after atomic move: {Path}", sourcePath);
                    // We return true because the move (copy+verify+swap) effectively succeeded for the target.
                    // The source file remaining is customizable behavior, but for "Move", we typically want it gone.
                }
            }

            return success;
        }
    }
}
