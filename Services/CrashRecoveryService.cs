using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services
{
    /// <summary>
    /// Logic layer for Crash Recovery.
    /// Orchestrates the recovery process by reading from CrashRecoveryJournal
    /// and deciding what to do with orphaned files or interrupted operations.
    /// </summary>
    public class CrashRecoveryService
    {
        private readonly ILogger<CrashRecoveryService> _logger;
        private readonly CrashRecoveryJournal _journal;
        private readonly Services.IO.IFileWriteService _safeWrite; // For cleaning up/finalizing

        public CrashRecoveryService(
            ILogger<CrashRecoveryService> logger,
            CrashRecoveryJournal journal,
            Services.IO.IFileWriteService safeWrite)
        {
            _logger = logger;
            _journal = journal;
            _safeWrite = safeWrite;
        }

        public async Task RecoverAsync()
        {
            _logger.LogInformation("🛡️ Starting Crash Recovery Protocol...");

            try
            {
                // 1. Get all pending checkpoints from the journal
                var checkpoints = await _journal.GetPendingCheckpointsAsync();
                
                if (!checkpoints.Any())
                {
                    _logger.LogInformation("✅ No pending crash checkpoints found. Clean startup.");
                    return;
                }

                _logger.LogWarning("⚠️ Found {Count} pending operations from previous session.", checkpoints.Count);

                int recoveredCount = 0;
                int failedCount = 0;

                foreach (var checkpoint in checkpoints)
                {
                    try
                    {
                        bool success = await ProcessCheckpointAsync(checkpoint);
                        if (success)
                        {
                            recoveredCount++;
                            await _journal.CompleteCheckpointAsync(checkpoint.Id);
                        }
                        else
                        {
                            failedCount++;
                            // If it failed recovery, we might mark it as dead letter or just leave it for next time?
                            // For now, let's increment failure count or mark dead letter if too many failures.
                            if (checkpoint.FailureCount >= 3)
                            {
                                await _journal.MarkAsDeadLetterAsync(checkpoint.Id);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing checkpoint {Id} ({Type})", checkpoint.Id, checkpoint.OperationType);
                        failedCount++;
                    }
                }

                _logger.LogInformation("🏁 Recovery Complete. Recovered: {Recovered}, Failed: {Failed}", recoveredCount, failedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error during crash recovery execution.");
            }
        }

        private async Task<bool> ProcessCheckpointAsync(RecoveryCheckpoint checkpoint)
        {
            _logger.LogInformation("Attempting recovery for {Type}: {Path}", checkpoint.OperationType, checkpoint.TargetPath);

            switch (checkpoint.OperationType)
            {
                case OperationType.TagWrite:
                    return await RecoverTagWriteAsync(checkpoint);
                
                case OperationType.Download:
                    // TODO: Implement Download Resume logic
                    // For now, checks if .part file is valid and maybe notifies DownloadManager?
                    // Currently DownloadManager handles its own resume via file existence check, 
                    // but we could clean up zombies here.
                    // Mark as handled for now to clear journal? Or leave it?
                    // Actually, for downloads, the journal should probably be cleared if we assume the DownloadManager 
                    // picked it up or started fresh. Let's just log it.
                    _logger.LogDebug("Download checkpoint found - deferring to DownloadManager scan.");
                    return true;

                default:
                    _logger.LogWarning("Unknown operation type: {Type}", checkpoint.OperationType);
                    return false;
            }
        }

        private async Task<bool> RecoverTagWriteAsync(RecoveryCheckpoint checkpoint)
        {
            // Logic for TagWrite recovery:
            // 1. Check if Temp file exists and is valid?
            // 2. Check if Target file is corrupted (size 0)?
            // 3. If Temp is good, complete the Move.
            // 4. If Temp is bad but Target is good, discard Temp.
            
            try 
            {
                var state = Newtonsoft.Json.JsonConvert.DeserializeObject<TagWriteCheckpointState>(checkpoint.StateJson);
                if (state == null) return false;

                bool tempExists = File.Exists(state.TempPath);
                bool targetExists = File.Exists(state.FilePath);

                if (tempExists)
                {
                    _logger.LogInformation("Found orphaned temp file for tag write: {TempPath}", state.TempPath);
                    
                    // If target is missing or zero-byte (corrupted), we assume intended replace failed.
                    if (!targetExists || new FileInfo(state.FilePath).Length == 0)
                    {
                        // Commit the temp file
                        _logger.LogInformation("Target file missing/empty. Committing temp file to {Path}", state.FilePath);
                        
                        // Use SafeWrite's MoveAtomic to finish the job
                        return await _safeWrite.MoveAtomicAsync(state.TempPath, state.FilePath);
                    }
                    else
                    {
                        // Both exist. Usually safe to delete temp and assume previous write finished or was abandoned?
                        // Or, we could compare timestamps.
                        // Safe approach: Delete temp to avoid confusion, user keeps original.
                        File.Delete(state.TempPath);
                        _logger.LogInformation("Target file exists and looks healthy. Deleted orphaned temp file.");
                        return true;
                    }
                }
                
                return true; // Nothing to do (Temp gone), so checkpoint is "done"
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover TagWrite");
                return false;
            }
        }
    }
}
