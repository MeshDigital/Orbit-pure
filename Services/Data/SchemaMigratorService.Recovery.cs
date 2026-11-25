using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Services.Models;

namespace SLSKDONET.Services;

/// <summary>Database recovery (health check, backup/restore, force-reset) — extracted from SchemaMigratorService (#307).</summary>
public partial class SchemaMigratorService
{
    private async Task<bool> IsSqliteDatabaseHealthyAsync(string dbPath)
    {
        if (!System.IO.File.Exists(dbPath))
            return false;

        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();

            using var conn = new SqliteConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check(1);";
            var result = (await cmd.ExecuteScalarAsync().ConfigureAwait(false))?.ToString();
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQLite health check failed for {DbPath}", dbPath);
            return false;
        }
    }

    private async Task<bool> TryRestoreLatestHealthyBackupAsync(string backupDir, string dbPath)
    {
        if (!System.IO.Directory.Exists(backupDir))
            return false;

        var backups = new System.IO.DirectoryInfo(backupDir)
            .GetFiles("library.backup.*.db")
            .OrderByDescending(f => f.CreationTime)
            .ToList();

        foreach (var backup in backups)
        {
            if (await IsSqliteDatabaseHealthyAsync(backup.FullName).ConfigureAwait(false))
            {
                await DeleteDatabaseFilesAsync(dbPath).ConfigureAwait(false);
                System.IO.File.Copy(backup.FullName, dbPath, overwrite: true);
                _logger.LogInformation("✅ Database restored from healthy backup: {Backup}", backup.Name);
                return true;
            }

            // quick_check failed — try REINDEX repair (handles index corruption with intact table data)
            _logger.LogWarning("Backup {Backup} failed quick_check. Attempting REINDEX repair...", backup.Name);
            var repairedPath = backup.FullName + ".repair-attempt.db";
            try
            {
                System.IO.File.Copy(backup.FullName, repairedPath, overwrite: true);
                var rwCs = new SqliteConnectionStringBuilder
                {
                    DataSource = repairedPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                    DefaultTimeout = 30
                }.ToString();

                using (var repairConn = new SqliteConnection(rwCs))
                {
                    await repairConn.OpenAsync().ConfigureAwait(false);
                    using var repairCmd = repairConn.CreateCommand();
                    repairCmd.CommandText = "REINDEX;";
                    await repairCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    repairCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await repairCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                SqliteConnection.ClearAllPools();

                if (await IsSqliteDatabaseHealthyAsync(repairedPath).ConfigureAwait(false))
                {
                    await DeleteDatabaseFilesAsync(dbPath).ConfigureAwait(false);
                    System.IO.File.Copy(repairedPath, dbPath, overwrite: true);
                    _logger.LogInformation("✅ Database restored from repaired backup: {Backup}", backup.Name);
                    return true;
                }

                _logger.LogWarning("Skipping corrupt backup candidate (repair failed): {Backup}", backup.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Repair attempt failed for backup: {Backup}", backup.Name);
            }
            finally
            {
                if (System.IO.File.Exists(repairedPath))
                    System.IO.File.Delete(repairedPath);
            }
        }

        return false;
    }

    private async Task DeleteDatabaseFilesAsync(string dbPath)
    {
        SqliteConnection.ClearAllPools();

        var files = new[]
        {
            dbPath,
            dbPath + "-wal",
            dbPath + "-shm"
        };

        foreach (var file in files)
        {
            if (!System.IO.File.Exists(file))
                continue;

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    System.IO.File.Delete(file);
                    break;
                }
                catch when (i < 2)
                {
                    await Task.Delay(150).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task PerformBackupAsync()
    {
        try
        {
            var appData = SLSKDONET.Data.OrbitPaths.RoamingRoot;
            var dbPath = SLSKDONET.Data.OrbitPaths.LibraryDbPath;
            var backupDir = LibraryDatabaseMaintenance.GetBackupDirectory(dbPath); // derived from the DB path so ORBIT_DB_PATH (tests) never restores/rotates the real backups

            if (!System.IO.File.Exists(dbPath))
            {
                // Auto-Restore Logic
                if (System.IO.Directory.Exists(backupDir))
                {
                    var restored = await TryRestoreLatestHealthyBackupAsync(backupDir, dbPath).ConfigureAwait(false);
                    if (restored)
                    {
                        _logger.LogWarning("⚠️ Database missing! Auto-restore succeeded from healthy backup.");
                        _logger.LogInformation("Initialization will now patch schema.");
                        return; // Done, we restored. No need to backup the thing we just restored immediately.
                    }
                }

                _logger.LogInformation("No existing database and no backups found. Starting fresh.");
                return;
            }

            if (!await IsSqliteDatabaseHealthyAsync(dbPath).ConfigureAwait(false))
            {
                _logger.LogWarning("Skipping backup creation because active database is malformed: {DbPath}", dbPath);
                return;
            }

            System.IO.Directory.CreateDirectory(backupDir);

            // Create new backup
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupPath = System.IO.Path.Combine(backupDir, $"library.backup.{timestamp}.db");

            // Use Copy to allow decent backup even if file checks fail later,
            // but wrap in Task.Run to not block startup significantly if large
            await Task.Run(async () =>
            {
                // SQLite online backup, not File.Copy: a WAL-mode DB after an unclean shutdown
                // can hold committed pages in library.db-wal that a plain copy would drop.
                await LibraryDatabaseMaintenance.CreateBackupAsync(dbPath, backupPath).ConfigureAwait(false);
                _logger.LogInformation("Database backed up to: {Path}", backupPath);

                // Rotate backups: Keep last 5
                var backups = new System.IO.DirectoryInfo(backupDir)
                    .GetFiles("library.backup.*.db")
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                if (backups.Count > 5)
                {
                    foreach (var oldBackup in backups.Skip(5))
                    {
                        try
                        {
                            oldBackup.Delete();
                            _logger.LogInformation("Deleted old backup: {Name}", oldBackup.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete old backup: {Name}", oldBackup.Name);
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform automatic database backup");
            // Do not throw, allow startup to continue
        }
    }

    /// <summary>Marker the Settings "Update Schema" action drops to request a reset on next launch. Lives next to the DB.</summary>
    public static string ForceResetMarkerPath(string? dbPath = null)
    {
        dbPath ??= SLSKDONET.Data.OrbitPaths.LibraryDbPath;
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dbPath)) ?? ".", ".force_schema_reset");
    }

    private async Task CheckForForceResetAsync()
    {
        try
        {
            var dbPath = SLSKDONET.Data.OrbitPaths.LibraryDbPath;
            var markerPath = ForceResetMarkerPath(dbPath);

            if (System.IO.File.Exists(markerPath))
            {
                _logger.LogWarning("⚠️ FORCE RESET MARKER FOUND! Deleting database to force schema rebuild...");

                // Try to delete the database
                if (System.IO.File.Exists(dbPath))
                {
                    // Basic retry loop in case of lingering locks
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            System.IO.File.Delete(dbPath);
                            _logger.LogInformation("✅ Database deleted via force reset.");
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Attempt {Retry} to delete database failed: {Message}", i + 1, ex.Message);
                            await Task.Delay(500);
                        }
                    }
                }

                // Clean up marker
                try { System.IO.File.Delete(markerPath); } catch (IOException ex) { _logger.LogDebug(ex, "Best-effort marker file cleanup failed for {Path}", markerPath); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process force reset marker");
        }
    }
}
