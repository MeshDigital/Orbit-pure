using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

public enum OperationType
{
    Download,
    TagWrite,
    MetadataHydration
}

public enum CheckpointStatus
{
    Active = 0,
    Completed = 1,
    DeadLetter = 2
}

public class RecoveryCheckpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public OperationType OperationType { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string StateJson { get; set; } = string.Empty;
    public int Priority { get; set; } = 0;
    public int FailureCount { get; set; } = 0;
    public CheckpointStatus Status { get; set; } = CheckpointStatus.Active;
    public long LastHeartbeat { get; set; } = Stopwatch.GetTimestamp();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DownloadCheckpointState
{
    public string TrackGlobalId { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SoulseekUsername { get; set; } = string.Empty;
    public string SoulseekFilename { get; set; } = string.Empty;
    public long ExpectedSize { get; set; }
    public string PartFilePath { get; set; } = string.Empty;
    public string FinalPath { get; set; } = string.Empty;
    public long BytesDownloaded { get; set; }
}

public class TagWriteCheckpointState
{
    public string FilePath { get; set; } = string.Empty;
    public string TempPath { get; set; } = string.Empty;
    public DateTime OriginalTimestamp { get; set; }
    public DateTime? OriginalCreationTime { get; set; } // Phase 2A: For full timestamp recovery
}

public struct SystemHealthStats
{
    public int ActiveCount { get; set; }
    public int DeadLetterCount { get; set; }
    public int RecoveredCount { get; set; } // Renamed from CompletedCount, tracks session recoveries
}

public class HydrationCheckpointState
{
    public string TrackGlobalId { get; set; } = string.Empty;
    public string SpotifyId { get; set; } = string.Empty;
    public int Step { get; set; } = 0; // 1: Metadata, 2: Artwork, 3: Audio Features
    public Dictionary<string, object> CollectedData { get; set; } = new();
}

/// <summary>
/// Phase 2A: Ironclad Crash Recovery Journal
/// Tracks in-progress operations for automatic recovery after crashes.
/// Uses monotonic timestamps, dead-letter handling, and connection pooling.
/// </summary>
public class CrashRecoveryJournal : IDisposable, IAsyncDisposable
{
    private readonly ILogger<CrashRecoveryJournal> _logger;
    private static readonly SemaphoreSlim _journalLock = new(1, 1);
    
    // PERFORMANCE: Shared connection for journal (reduces overhead)
    private SqliteConnection? _journalConnection;
    private SqliteCommand? _insertCheckpointCmd;
    private SqliteCommand? _updateHeartbeatCmd;
    private SqliteCommand? _deleteCheckpointCmd;
    private SqliteCommand? _resetFailureCountCmd;
    private SqliteCommand? _markDeadLetterCmd;
    private SqliteCommand? _getSystemHealthCmd;
    
    private bool _disposed = false;

    public CrashRecoveryJournal(ILogger<CrashRecoveryJournal> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initializes the recovery journal table and connection pool.
    /// </summary>
    public async Task InitAsync()
    {
        try
        {
            // Get database path
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dbPath = Path.Combine(appData, "ORBIT", "library.db");
            
            // Create table using main context
            using var context = new AppDbContext();
            var connection = context.Database.GetDbConnection() as SqliteConnection;
            await connection!.OpenAsync();

            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS RecoveryCheckpoints (
                    Id TEXT PRIMARY KEY,
                    OperationType TEXT NOT NULL,
                    TargetPath TEXT NOT NULL,
                    StateJson TEXT NOT NULL,
                    Priority INTEGER DEFAULT 0,
                    FailureCount INTEGER DEFAULT 0,
                    Status INTEGER DEFAULT 0, -- 0: Active, 1: Completed, 2: DeadLetter
                    LastHeartbeat INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                
                CREATE INDEX IF NOT EXISTS IX_Recovery_Priority 
                ON RecoveryCheckpoints(Priority DESC);
                
                CREATE INDEX IF NOT EXISTS IX_Recovery_Type 
                ON RecoveryCheckpoints(OperationType);
                
                CREATE INDEX IF NOT EXISTS IX_Recovery_Heartbeat 
                ON RecoveryCheckpoints(LastHeartbeat);
            ";
            await createCmd.ExecuteNonQueryAsync();

            // PERFORMANCE: Create dedicated journal connection
            _journalConnection = new SqliteConnection($"Data Source={dbPath}");
            await _journalConnection.OpenAsync();
            
            // PERFORMANCE: Journal-specific optimizations
            using var pragmaCmd = _journalConnection.CreateCommand();
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
                PRAGMA mmap_size = 268435456; -- 256MB Limit (Good Neighbor Policy)
            ";
            await pragmaCmd.ExecuteNonQueryAsync();

            // PERFORMANCE: Pre-compile frequently used statements
            _insertCheckpointCmd = _journalConnection.CreateCommand();
            _insertCheckpointCmd.CommandText = @"
                INSERT OR REPLACE INTO RecoveryCheckpoints 
                (Id, OperationType, TargetPath, StateJson, Priority, FailureCount, Status, LastHeartbeat, CreatedAt)
                VALUES (@id, @type, @path, @state, @priority, @failures, @status, @heartbeat, @created)";
            _insertCheckpointCmd.Prepare();

            _updateHeartbeatCmd = _journalConnection.CreateCommand();
            _updateHeartbeatCmd.CommandText = @"
                UPDATE RecoveryCheckpoints 
                SET LastHeartbeat = @heartbeat, StateJson = @state 
                WHERE Id = @id";
            _updateHeartbeatCmd.Prepare();

            _deleteCheckpointCmd = _journalConnection.CreateCommand();
            _deleteCheckpointCmd.CommandText = "DELETE FROM RecoveryCheckpoints WHERE Id = @id";
            _deleteCheckpointCmd.Prepare();

            _resetFailureCountCmd = _journalConnection.CreateCommand();
            _resetFailureCountCmd.CommandText = "UPDATE RecoveryCheckpoints SET FailureCount = 0, Status = 0 WHERE TargetPath = @path";
            _resetFailureCountCmd.Prepare();
            
            _markDeadLetterCmd = _journalConnection.CreateCommand();
            _markDeadLetterCmd.CommandText = "UPDATE RecoveryCheckpoints SET Status = 2 WHERE Id = @id";
            _markDeadLetterCmd.Prepare();
            
            _getSystemHealthCmd = _journalConnection.CreateCommand();
            _getSystemHealthCmd.CommandText = @"
                SELECT Status, COUNT(*) 
                FROM RecoveryCheckpoints 
                GROUP BY Status";
            _getSystemHealthCmd.Prepare();

            _logger.LogInformation("✅ Ironclad Recovery Journal initialized with optimized connection pool");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize crash recovery journal");
            throw;
        }
    }

    /// <summary>
    /// Logs a checkpoint with INSERT OR REPLACE (idempotent).
    /// </summary>
    public async Task<string> LogCheckpointAsync(RecoveryCheckpoint checkpoint)
    {
        if (_disposed || _insertCheckpointCmd == null)
            throw new ObjectDisposedException(nameof(CrashRecoveryJournal));

        await _journalLock.WaitAsync();
        try
        {
            _insertCheckpointCmd.Parameters.Clear();
            _insertCheckpointCmd.Parameters.AddWithValue("@id", checkpoint.Id);
            _insertCheckpointCmd.Parameters.AddWithValue("@type", checkpoint.OperationType.ToString());
            _insertCheckpointCmd.Parameters.AddWithValue("@path", checkpoint.TargetPath);
            _insertCheckpointCmd.Parameters.AddWithValue("@state", checkpoint.StateJson);
            _insertCheckpointCmd.Parameters.AddWithValue("@priority", checkpoint.Priority);
            _insertCheckpointCmd.Parameters.AddWithValue("@failures", checkpoint.FailureCount);
            _insertCheckpointCmd.Parameters.AddWithValue("@status", (int)checkpoint.Status);
            _insertCheckpointCmd.Parameters.AddWithValue("@heartbeat", Stopwatch.GetTimestamp());
            _insertCheckpointCmd.Parameters.AddWithValue("@created", checkpoint.CreatedAt.ToString("o"));

            await _insertCheckpointCmd.ExecuteNonQueryAsync();
            
            _logger.LogDebug("Logged checkpoint: {Type} - {Path} (Priority: {Priority})", 
                checkpoint.OperationType, checkpoint.TargetPath, checkpoint.Priority);
            
            return checkpoint.Id;
        }
        finally
        {
            _journalLock.Release();
        }
    }

    /// <summary>
    /// PERFORMANCE: Only updates heartbeat if BytesDownloaded changed (SSD save trick).
    /// </summary>
    public async Task UpdateHeartbeatAsync(string checkpointId, string updatedStateJson, long previousBytes, long currentBytes)
    {
        if (_disposed || _updateHeartbeatCmd == null)
            return; // Silent fail during shutdown

        // OPTIMIZATION: Skip if no progress (saves SSD write cycles)
        if (previousBytes == currentBytes)
        {
            _logger.LogTrace("Skipping heartbeat update for {Id} - no progress", checkpointId);
            return;
        }

        await _journalLock.WaitAsync();
        try
        {
            _updateHeartbeatCmd.Parameters.Clear();
            _updateHeartbeatCmd.Parameters.AddWithValue("@heartbeat", Stopwatch.GetTimestamp());
            _updateHeartbeatCmd.Parameters.AddWithValue("@state", updatedStateJson);
            _updateHeartbeatCmd.Parameters.AddWithValue("@id", checkpointId);

            await _updateHeartbeatCmd.ExecuteNonQueryAsync();
            
            _logger.LogTrace("Updated heartbeat for {Id}: {Current}/{Previous} bytes", 
                checkpointId, currentBytes, previousBytes);
        }
        finally
        {
            _journalLock.Release();
        }
    }

    /// <summary>
    /// Removes a checkpoint after successful completion.
    /// </summary>
    private int _sessionRecoveredCount = 0;

    /// <summary>
    /// Removes a checkpoint after successful completion.
    /// </summary>
    public async Task CompleteCheckpointAsync(string checkpointId)
    {
        if (_disposed || _deleteCheckpointCmd == null)
            return; // Silent fail during shutdown

        await _journalLock.WaitAsync();
        try
        {
            _deleteCheckpointCmd.Parameters.Clear();
            _deleteCheckpointCmd.Parameters.AddWithValue("@id", checkpointId);
            
            var rowsAffected = await _deleteCheckpointCmd.ExecuteNonQueryAsync();
            
            if (rowsAffected > 0)
            {
                Interlocked.Increment(ref _sessionRecoveredCount);
                _logger.LogDebug("✅ Completed checkpoint: {Id}", checkpointId);
            }
        }
        finally
        {
            _journalLock.Release();
        }
    }

    /// <summary>
    /// Resets failure count to 0 for a given target path (manual retry).
    /// </summary>
    public async Task ResetFailureCountAsync(string targetPath)
    {
        if (_disposed || _resetFailureCountCmd == null)
            return;

        await _journalLock.WaitAsync();
        try
        {
            _resetFailureCountCmd.Parameters.Clear();
            _resetFailureCountCmd.Parameters.AddWithValue("@path", targetPath);
            
            var rows = await _resetFailureCountCmd.ExecuteNonQueryAsync();
            if (rows > 0)
            {
                _logger.LogInformation("🔄 Reset failure count for checkout path: {Path}", targetPath);
            }
        }
        finally
        {
            _journalLock.Release();
        }
    }

    /// <summary>
    /// Phase 2A/3A: Marks a checkpoint as "DeadLetter" (Status=2) to preserve it in DB
    /// instead of deleting it. Prevents startup loops while keeping audit trail.
    /// </summary>
    public async Task MarkAsDeadLetterAsync(string checkpointId)
    {
        if (_disposed || _markDeadLetterCmd == null)
            return;

        await _journalLock.WaitAsync();
        try
        {
            _markDeadLetterCmd.Parameters.Clear();
            _markDeadLetterCmd.Parameters.AddWithValue("@id", checkpointId);
            
            var rows = await _markDeadLetterCmd.ExecuteNonQueryAsync();
            if (rows > 0)
            {
                _logger.LogWarning("🏴 Marked checkpoint as Dead-Letter: {Id}", checkpointId);
            }
        }
        finally
        {
            _journalLock.Release();
        }
    }

    /// <summary>
    /// Phase 3A: Atomic Resume - Gets confirmed bytes from journal.
    /// Acts as the "Source of Truth" to validate partial files on disk.
    /// Uses JsonDocument to avoid dependency on specific state types.
    /// </summary>
    public async Task<long> GetConfirmedBytesAsync(string checkpointId)
    {
        if (_disposed || _journalConnection == null)
            return 0;

        await _journalLock.WaitAsync();
        try
        {
            using var cmd = _journalConnection.CreateCommand();
            cmd.CommandText = "SELECT StateJson FROM RecoveryCheckpoints WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", checkpointId);

            var json = await cmd.ExecuteScalarAsync() as string;
            if (string.IsNullOrEmpty(json))
                return 0;

            try 
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("BytesDownloaded", out var bytesElement))
                {
                    return bytesElement.GetInt64();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse confirmed bytes for checkpoint {Id}", checkpointId);
            }

            return 0;
        }
        finally
        {
            _journalLock.Release();
        }
    }

    /// <summary>
    /// Gets all pending checkpoints for recovery, sorted by priority.
    /// Filters out stale checkpoints based on monotonic timestamp.
    /// </summary>
    public async Task<List<RecoveryCheckpoint>> GetPendingCheckpointsAsync()
    {
        if (_disposed || _journalConnection == null)
            return new List<RecoveryCheckpoint>();

        var checkpoints = new List<RecoveryCheckpoint>();
        
        // Calculate stale threshold: 24 hours ago in Stopwatch ticks
        var ticksPerSecond = Stopwatch.Frequency;
        var staleThresholdTicks = Stopwatch.GetTimestamp() - (ticksPerSecond * 60 * 60 * 24); // 24 hours

        using var cmd = _journalConnection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, OperationType, TargetPath, StateJson, Priority, FailureCount, 
                   LastHeartbeat, CreatedAt, Status
            FROM RecoveryCheckpoints
            WHERE LastHeartbeat > @staleThreshold AND Status = 0
            ORDER BY Priority DESC, CreatedAt ASC";
        cmd.Parameters.AddWithValue("@staleThreshold", staleThresholdTicks);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            checkpoints.Add(new RecoveryCheckpoint
            {
                Id = reader.GetString(0),
                OperationType = Enum.Parse<OperationType>(reader.GetString(1)),
                TargetPath = reader.GetString(2),
                StateJson = reader.GetString(3),
                Priority = reader.GetInt32(4),
                FailureCount = reader.GetInt32(5),
                LastHeartbeat = reader.GetInt64(6),
                CreatedAt = DateTime.Parse(reader.GetString(7)),
                Status = (CheckpointStatus)reader.GetInt32(8)
            });
        }

        return checkpoints;
    }

    /// <summary>
    /// Phase 3A (Transparency): Gets aggregated health stats for the UI Dashboard.
    /// Helps users see if there are pending dead letters or active recoveries.
    /// </summary>
    public async Task<SystemHealthStats> GetSystemHealthAsync()
    {
        if (_disposed || _journalConnection == null || _getSystemHealthCmd == null)
            return new SystemHealthStats();

        var stats = new SystemHealthStats();

        await _journalLock.WaitAsync();
        try
        {
            using var reader = await _getSystemHealthCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var status = (CheckpointStatus)reader.GetInt32(0);
                var count = reader.GetInt32(1);

                switch (status)
                {
                    case CheckpointStatus.Active:
                        stats.ActiveCount = count;
                        break;
                    case CheckpointStatus.DeadLetter:
                        stats.DeadLetterCount = count;
                        break;
                }
            }
            stats.RecoveredCount = _sessionRecoveredCount;
            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve system health stats");
            return new SystemHealthStats();
        }
        finally
        {
            _journalLock.Release();
        }
    }

    /// <summary>
    /// Phase 3B: User Action to Retry Dead-Lettered tracks.
    /// Resets 'DeadLetter' (Status=3) entries back to 'Prepared' (Status=0).
    /// </summary>
    public async Task<int> ResetDeadLettersAsync(int batchSize = 50)
    {
        if (_disposed || _journalConnection == null)
            return 0;

        await _journalLock.WaitAsync();
        try
        {
            // Safer LIMIT subquery for SQLite update
            using var cmd = _journalConnection.CreateCommand();
            cmd.CommandText = @"
                UPDATE RecoveryCheckpoints
                SET Status = 0, LastHeartbeat = @now, FailureCount = 0
                WHERE Id IN (
                    SELECT Id FROM RecoveryCheckpoints 
                    WHERE Status = 3
                    LIMIT @limit
                )";
             
             cmd.Parameters.AddWithValue("@now", Stopwatch.GetTimestamp());
             cmd.Parameters.AddWithValue("@limit", batchSize);

             int count = await cmd.ExecuteNonQueryAsync();
             if (count > 0)
             {
                 _logger.LogInformation("🔄 Reset {Count} dead-lettered checkpoints for retry", count);
             }
             return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset dead letters");
            return 0;
        }
        finally
        {
            _journalLock.Release();
        }
    }

    /// <summary>
    /// Clears all stale checkpoints (heartbeat >24 hours old).
    /// Used during startup to clean up truly abandoned operations.
    /// </summary>
    public async Task ClearStaleCheckpointsAsync()
    {
        if (_disposed || _journalConnection == null)
            return;

        await _journalLock.WaitAsync();
        try
        {
            var ticksPerSecond = Stopwatch.Frequency;
            var staleThresholdTicks = Stopwatch.GetTimestamp() - (ticksPerSecond * 60 * 60 * 24); // 24 hours

            using var cmd = _journalConnection.CreateCommand();
            cmd.CommandText = "DELETE FROM RecoveryCheckpoints WHERE LastHeartbeat < @threshold";
            cmd.Parameters.AddWithValue("@threshold", staleThresholdTicks);
            
            var deleted = await cmd.ExecuteNonQueryAsync();
            if (deleted > 0)
            {
                _logger.LogWarning("🗑️ Cleared {Count} stale checkpoints (>24 hours old)", deleted);
            }
        }
        finally
        {
            _journalLock.Release();
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _insertCheckpointCmd?.Dispose();
            _updateHeartbeatCmd?.Dispose();
            _deleteCheckpointCmd?.Dispose();
            _resetFailureCountCmd?.Dispose();
            _markDeadLetterCmd?.Dispose();
            _getSystemHealthCmd?.Dispose();
            _journalConnection?.Dispose();
            _journalLock.Dispose();
        }


        _disposed = true;
        _logger.LogInformation("Crash recovery journal disposed");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _insertCheckpointCmd?.Dispose();
        _updateHeartbeatCmd?.Dispose();
        _deleteCheckpointCmd?.Dispose();
        _resetFailureCountCmd?.Dispose();
        _markDeadLetterCmd?.Dispose();
        _getSystemHealthCmd?.Dispose();
        
        if (_journalConnection != null)
        {
            await _journalConnection.DisposeAsync();
        }
        
        _journalLock.Dispose();

        _disposed = true;
        _logger.LogInformation("Crash recovery journal disposed asynchronously");
        
        GC.SuppressFinalize(this);
    }
}
