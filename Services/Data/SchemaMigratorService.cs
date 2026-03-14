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

public class SchemaMigratorService
{
    private readonly ILogger<SchemaMigratorService> _logger;
    private static readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);

    public SchemaMigratorService(ILogger<SchemaMigratorService> logger)
    {
        _logger = logger;
    }

    private async Task PerformBackupAsync()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dbPath = System.IO.Path.Combine(appData, "ORBIT", "library.db");
            var backupDir = System.IO.Path.Combine(appData, "ORBIT", "Backups");

            if (!System.IO.File.Exists(dbPath))
            {
                // Auto-Restore Logic
                if (System.IO.Directory.Exists(backupDir))
                {
                    var latestBackup = new System.IO.DirectoryInfo(backupDir)
                        .GetFiles("library.backup.*.db")
                        .OrderByDescending(f => f.CreationTime)
                        .FirstOrDefault();

                    if (latestBackup != null)
                    {
                        _logger.LogWarning("⚠️ Database missing! Implementing Auto-Restore from: {Backup}", latestBackup.Name);
                        System.IO.File.Copy(latestBackup.FullName, dbPath);
                        _logger.LogInformation("✅ Database restored successfully. Initialization will now patch schema.");
                        return; // Done, we restored. No need to backup the thing we just restored immediately.
                    }
                }

                _logger.LogInformation("No existing database and no backups found. Starting fresh.");
                return;
            }

            System.IO.Directory.CreateDirectory(backupDir);

            // Create new backup
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupPath = System.IO.Path.Combine(backupDir, $"library.backup.{timestamp}.db");

            // Use Copy to allow decent backup even if file checks fail later,
            // but wrap in Task.Run to not block startup significantly if large
            await Task.Run(() =>
            {
                System.IO.File.Copy(dbPath, backupPath, overwrite: true);
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

    private async Task CheckForForceResetAsync()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var markerPath = System.IO.Path.Combine(appData, "ORBIT", ".force_schema_reset");
            var dbPath = System.IO.Path.Combine(appData, "ORBIT", "library.db");

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
                try { System.IO.File.Delete(markerPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process force reset marker");
        }
    }

    public async Task InitializeDatabaseAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[{Ms}ms] Database Init: Starting", sw.ElapsedMilliseconds);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dbPath = Path.Combine(appData, "ORBIT", "library.db");

        // Phase 24: Automatic Database Backup & Recovery
        await CheckForForceResetAsync().ConfigureAwait(false); // Step 1: Check if user requested reset
        await PerformBackupAsync().ConfigureAwait(false);      // Step 2: Backup existing or Restore if missing

        // Use a dedicated connection string WITHOUT pooling for the ENTIRE initialization process
        // This prevents lingering locks from pooled connections between migration steps.
        var initConnectionString = $"Data Source={dbPath};Default Timeout=30000;Pooling=False";

        // Initialize optimizations and WAL mode FIRST
        try
        {
            using (var conn = new SqliteConnection(initConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000; PRAGMA wal_checkpoint(TRUNCATE);";
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            _logger.LogInformation("[{Ms}ms] Database Init: WAL mode applied and Checkpoint (TRUNCATE) completed.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply early database optimizations.");
        }

        // Phase 12: Transition to EF Core Migrations
        _logger.LogInformation("[{Ms}ms] Database Init: Checking for legacy database...", sw.ElapsedMilliseconds);
        bool legacyDbExists = false;
        try
        {
            using (var conn = new SqliteConnection(initConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Tracks';";
                var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                legacyDbExists = (long)(result ?? 0) > 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy check failed (expected for fresh DB)");
        }

            if (legacyDbExists)
            {
                _logger.LogInformation("[{Ms}ms] Database Init: Legacy table 'Tracks' found. Checking migration record...", sw.ElapsedMilliseconds);
                bool historyExists = false;
                try
                {
                    using (var conn = new SqliteConnection(initConnectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
                        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                        historyExists = (long)(result ?? 0) > 0;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "History table check failed");
                }

            if (!historyExists)
            {
                _logger.LogWarning("Legacy manually-patched database detected. Bootstrapping EF migrations history.");

                using (var conn = new SqliteConnection(initConnectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS ""LibraryFolders"" (
                            ""Id"" TEXT NOT NULL PRIMARY KEY,
                            ""FolderPath"" TEXT NOT NULL,
                            ""IsEnabled"" INTEGER NOT NULL DEFAULT 1,
                            ""AddedAt"" TEXT NOT NULL,
                            ""LastScannedAt"" TEXT NULL,
                            ""TracksFound"" INTEGER NOT NULL DEFAULT 0
                        );
                        CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                            ""MigrationId"" TEXT NOT NULL PRIMARY KEY,
                            ""ProductVersion"" TEXT NOT NULL
                        );
                        INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                        VALUES ('20260107122524_InitialStructure', '9.0.0');";
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        // Phase 24.5: Clear stale migration locks if they exist
        try
        {
            using (var conn = new SqliteConnection(initConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock';";
                var lockTableExists = await cmd.ExecuteScalarAsync().ConfigureAwait(false) != null;
                if (lockTableExists)
                {
                    cmd.CommandText = "DELETE FROM __EFMigrationsLock;";
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    _logger.LogInformation("[{Ms}ms] Stale migration lock cleared from '__EFMigrationsLock'.", sw.ElapsedMilliseconds);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear migration locks (non-fatal).");
        }

        // Critical Fix for Schema Drift:
        // Consolidating checks for columns that might have been added manually or via failed migrations
        try
        {
            using (var conn = new SqliteConnection(initConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                
                // 1. Check for IsUserPaused migration drift
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('PlaylistTracks') WHERE name='IsUserPaused'";
                bool isUserPausedExists = (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0) > 0;
                if (isUserPausedExists)
                {
                     cmd.CommandText = @"INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                                        VALUES ('20260212000254_AddIsUserPausedToPlaylistTrack', '9.0.0');";
                     await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                     _logger.LogInformation("[{Ms}ms] Schema fix: 'IsUserPaused' detected, migration record forced.", sw.ElapsedMilliseconds);
                }

                // 2. Check for VocalDensity migration drift (the current hang suspect)
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('audio_features') WHERE name='VocalDensity'";
                bool vocalDensityExists = (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0) > 0;
                if (vocalDensityExists)
                {
                     cmd.CommandText = @"INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                                        VALUES ('20260224160926_AddVocalDensityToAudioFeatures', '9.0.0');";
                     await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                     _logger.LogInformation("[{Ms}ms] Schema fix: 'VocalDensity' detected, migration record forced.", sw.ElapsedMilliseconds);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check/patch migration history drift.");
        }

        // Phase 12: Transition to EF Core Migrations
        var migrationOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(initConnectionString)
            .Options;

        List<string> pending;
        using (var context = new AppDbContext(migrationOptions))
        {
            pending = (await context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).ToList();
        }

        if (pending.Any())
        {
            _logger.LogInformation("[{Ms}ms] Pending migrations: {Migrations}", sw.ElapsedMilliseconds, string.Join(", ", pending));
            _logger.LogInformation("[{Ms}ms] Database Init: Calling MigrateAsync() on {DbPath}...", sw.ElapsedMilliseconds, dbPath);
            
            // Clear all connection pools just in case anything else touched the DB
            SqliteConnection.ClearAllPools();
            
            using (var context = new AppDbContext(migrationOptions))
            {
                var migrateTask = context.Database.MigrateAsync();
                var timeoutLimit = 600; 
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutLimit));
                
                if (await Task.WhenAny(migrateTask, timeoutTask) == timeoutTask)
                {
                    _logger.LogError("CRITICAL: Database Migration TIMEOUT ({Timeout}s).", timeoutLimit);
                }
                
                await migrateTask.ConfigureAwait(false);
            }
            _logger.LogInformation("[{Ms}ms] Database Init: EF Migrations applied successfully", sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogInformation("[{Ms}ms] Database Init: No pending migrations.", sw.ElapsedMilliseconds);
        }

        // Re-open for post-migration optimizations
        using (var context = new AppDbContext(migrationOptions))
        {
            var db = context.Database;
            var connection = db.GetDbConnection();
            if (connection != null)
            {
                context.ConfigureSqliteOptimizations(connection);
                await ApplySchemaPatchesAsync(context, connection);
            }
        }

        // Index Audit (DEBUG builds only)
#if DEBUG
        try
        {
            var auditReport = await AuditDatabaseIndexesAsync();
            if (auditReport.MissingIndexes.Any())
            {
                _logger.LogWarning("⚠️ Found {Count} missing indexes. Auto-applying...",
                    auditReport.MissingIndexes.Count);
                await ApplyIndexRecommendationsAsync(auditReport);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index audit failed (non-fatal)");
        }
#endif

        _logger.LogInformation("[{Ms}ms] Database initialization completed successfully", sw.ElapsedMilliseconds);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<IndexAuditReport> AuditDatabaseIndexesAsync()
    {
        var report = new IndexAuditReport
        {
            AuditDate = DateTime.Now,
            ExistingIndexes = new List<string>(),
            MissingIndexes = new List<IndexRecommendation>(),
            UnusedIndexes = new List<string>()
        };

        try
        {
            using var context = new AppDbContext();
            var connection = context.Database.GetDbConnection() as SqliteConnection;
            if (connection == null) return report;
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT name, tbl_name, sql
                    FROM sqlite_master
                    WHERE type='index' AND sql IS NOT NULL
                    ORDER BY tbl_name, name;";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var indexName = reader.GetString(0);
                    var tableName = reader.GetString(1);
                    report.ExistingIndexes.Add($"{tableName}.{indexName}");
                }
            }

            var recommendations = GetDefaultIndexRecommendations();

            foreach (var rec in recommendations)
            {
                var indexKey = $"{rec.TableName}.{string.Join("_", rec.ColumnNames)}";
                var exists = report.ExistingIndexes.Any(idx =>
                    idx.Contains(rec.TableName, StringComparison.OrdinalIgnoreCase) &&
                    rec.ColumnNames.All(col => idx.Contains(col, StringComparison.OrdinalIgnoreCase)));

                if (!exists)
                {
                    report.MissingIndexes.Add(rec);
                }
            }

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Index audit failed");
            throw;
        }
    }

    private List<IndexRecommendation> GetDefaultIndexRecommendations()
    {
        return new List<IndexRecommendation>
        {
            new()
            {
                TableName = "PlaylistTracks",
                ColumnNames = new[] { "PlaylistId", "Status" },
                Reason = "Composite index for filtered playlist queries",
                EstimatedImpact = "High",
                CreateIndexSql = "CREATE INDEX IF NOT EXISTS IX_PlaylistTrack_PlaylistId_Status ON PlaylistTracks(PlaylistId, Status);"
            },
            new()
            {
                TableName = "LibraryEntries",
                ColumnNames = new[] { "UniqueHash" },
                Reason = "Global library lookups for cross-project deduplication",
                EstimatedImpact = "High",
                CreateIndexSql = "CREATE INDEX IF NOT EXISTS IX_LibraryEntry_UniqueHash ON LibraryEntries(UniqueHash);"
            },
            new()
            {
                TableName = "LibraryEntries",
                ColumnNames = new[] { "Artist", "Title" },
                Reason = "Search and filtering in All Tracks view",
                EstimatedImpact = "Medium",
                CreateIndexSql = "CREATE INDEX IF NOT EXISTS IX_LibraryEntry_Artist_Title ON LibraryEntries(Artist, Title);"
            },
            new()
            {
                TableName = "Projects",
                ColumnNames = new[] { "IsDeleted", "CreatedAt" },
                Reason = "Filtered project listing",
                EstimatedImpact = "Medium",
                CreateIndexSql = "CREATE INDEX IF NOT EXISTS IX_Project_IsDeleted_CreatedAt ON Projects(IsDeleted, CreatedAt);"
            },
        };
    }

    public async Task ApplyIndexRecommendationsAsync(IndexAuditReport report)
    {
        using var context = new AppDbContext();
        var connection = context.Database.GetDbConnection() as SqliteConnection;
        if (connection == null) return;
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        foreach (var rec in report.MissingIndexes)
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = rec.CreateIndexSql;
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create index: {Sql}", rec.CreateIndexSql);
            }
        }
    }

    private async Task ApplySchemaPatchesAsync(AppDbContext context, System.Data.Common.DbConnection connection)
    {
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync();

            using var command = connection.CreateCommand();

            // Helper to check if column exists
            bool ColumnExists(string tableName, string columnName)
            {
                using var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE LOWER(name)=LOWER('{columnName}')";
                var result = checkCmd.ExecuteScalar();
                return Convert.ToInt32(result) > 0;
            }

            // Helper to check if table exists
            bool TableExists(string tableName)
            {
                using var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
                var result = checkCmd.ExecuteScalar();
                return Convert.ToInt32(result) > 0;
            }

            // 1. TechnicalDetails Table
            if (!TableExists("TechnicalDetails"))
            {
                _logger.LogInformation("Patching Schema: Creating TechnicalDetails table...");
                command.CommandText = @"
                    CREATE TABLE ""TechnicalDetails"" (
                        ""Id"" TEXT NOT NULL CONSTRAINT ""PK_TechnicalDetails"" PRIMARY KEY,
                        ""PlaylistTrackId"" TEXT NOT NULL,
                        ""WaveformData"" BLOB NULL,
                        ""RmsData"" BLOB NULL,
                        ""LowData"" BLOB NULL,
                        ""MidData"" BLOB NULL,
                        ""HighData"" BLOB NULL,
                        ""AiEmbeddingJson"" TEXT NULL,
                        ""CuePointsJson"" TEXT NULL,
                        ""AudioFingerprint"" TEXT NULL,
                        ""SpectralHash"" TEXT NULL,
                        ""LastUpdated"" TEXT NOT NULL,
                        ""IsPrepared"" INTEGER NOT NULL DEFAULT 0,
                        ""PrimaryGenre"" TEXT NULL,
                        CONSTRAINT ""FK_TechnicalDetails_PlaylistTracks_PlaylistTrackId"" FOREIGN KEY (""PlaylistTrackId"") REFERENCES ""PlaylistTracks"" (""Id"") ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX ""IX_TechnicalDetails_PlaylistTrackId"" ON ""TechnicalDetails"" (""PlaylistTrackId"");
                ";
                await command.ExecuteNonQueryAsync();
            }

            // Phase: Inbox Overhaul - Stalled Detection
            if (!ColumnExists("PlaylistTracks", "StalledReason"))
            {
                _logger.LogInformation("Patching Schema: Adding StalledReason to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""StalledReason"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }

            if (!ColumnExists("Tracks", "StalledReason"))
            {
                _logger.LogInformation("Patching Schema: Adding StalledReason to Tracks...");
                command.CommandText = @"ALTER TABLE ""Tracks"" ADD COLUMN ""StalledReason"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }

            // Phase: FLAC-Gold Download Resilience & Failure Escalation
            if (!ColumnExists("Tracks", "SearchRetryCount"))
            {
                _logger.LogInformation("Patching Schema: Adding SearchRetryCount to Tracks...");
                command.CommandText = @"ALTER TABLE ""Tracks"" ADD COLUMN ""SearchRetryCount"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("Tracks", "NotFoundRestartCount"))
            {
                _logger.LogInformation("Patching Schema: Adding NotFoundRestartCount to Tracks...");
                command.CommandText = @"ALTER TABLE ""Tracks"" ADD COLUMN ""NotFoundRestartCount"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "SearchRetryCount"))
            {
                _logger.LogInformation("Patching Schema: Adding SearchRetryCount to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""SearchRetryCount"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "NotFoundRestartCount"))
            {
                _logger.LogInformation("Patching Schema: Adding NotFoundRestartCount to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""NotFoundRestartCount"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }

            // Phase: Engagement, Intelligence & Sonic Tracking
            var primaryTables = new[] { "Tracks", "PlaylistTracks", "LibraryEntries" };
            foreach (var table in primaryTables)
            {
                // Engagement fields
                if (!ColumnExists(table, "IsLiked"))
                {
                    _logger.LogInformation("Patching Schema: Adding IsLiked to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"IsLiked\" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists(table, "Rating"))
                {
                    _logger.LogInformation("Patching Schema: Adding Rating to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"Rating\" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists(table, "PlayCount"))
                {
                    _logger.LogInformation("Patching Schema: Adding PlayCount to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"PlayCount\" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists(table, "LastPlayedAt"))
                {
                    _logger.LogInformation("Patching Schema: Adding LastPlayedAt to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"LastPlayedAt\" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }

                // AI & Sonic Intelligence fields
                if (!ColumnExists(table, "InstrumentalProbability"))
                {
                    _logger.LogInformation("Patching Schema: Adding InstrumentalProbability to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"InstrumentalProbability\" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists(table, "DetectedSubGenre"))
                {
                    _logger.LogInformation("Patching Schema: Adding DetectedSubGenre to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"DetectedSubGenre\" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists(table, "SubGenreConfidence"))
                {
                    _logger.LogInformation("Patching Schema: Adding SubGenreConfidence to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"SubGenreConfidence\" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists(table, "PrimaryGenre"))
                {
                    _logger.LogInformation("Patching Schema: Adding PrimaryGenre to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"PrimaryGenre\" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists(table, "DropTimestamp"))
                {
                    _logger.LogInformation("Patching Schema: Adding DropTimestamp to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"DropTimestamp\" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists(table, "ManualEnergy"))
                {
                    _logger.LogInformation("Patching Schema: Adding ManualEnergy to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"ManualEnergy\" INTEGER NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists(table, "SourceProvenance"))
                {
                    _logger.LogInformation("Patching Schema: Adding SourceProvenance to {Table}...", table);
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"SourceProvenance\" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }

                // Vocal Intelligence (Currently only in PlaylistTracks and LibraryEntries)
                if (table != "Tracks")
                {
                    if (!ColumnExists(table, "VocalType"))
                    {
                        _logger.LogInformation("Patching Schema: Adding VocalType to {Table}...", table);
                        command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"VocalType\" INTEGER NOT NULL DEFAULT 0;";
                        await command.ExecuteNonQueryAsync();
                    }
                    if (!ColumnExists(table, "VocalIntensity"))
                    {
                        _logger.LogInformation("Patching Schema: Adding VocalIntensity to {Table}...", table);
                        command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"VocalIntensity\" REAL NULL;";
                        await command.ExecuteNonQueryAsync();
                    }
                    if (!ColumnExists(table, "VocalStartSeconds"))
                    {
                        _logger.LogInformation("Patching Schema: Adding VocalStartSeconds to {Table}...", table);
                        command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"VocalStartSeconds\" REAL NULL;";
                        await command.ExecuteNonQueryAsync();
                    }
                    if (!ColumnExists(table, "VocalEndSeconds"))
                    {
                        _logger.LogInformation("Patching Schema: Adding VocalEndSeconds to {Table}...", table);
                        command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"VocalEndSeconds\" REAL NULL;";
                        await command.ExecuteNonQueryAsync();
                    }
                }
            }

            // 1C. StemPreferences Table (Phase 5: Engagement)
            if (!TableExists("StemPreferences"))
            {
                _logger.LogInformation("Patching Schema: Creating StemPreferences table...");
                command.CommandText = @"
                    CREATE TABLE ""StemPreferences"" (
                        ""Id"" TEXT NOT NULL CONSTRAINT ""PK_StemPreferences"" PRIMARY KEY,
                        ""TrackUniqueHash"" TEXT NOT NULL,
                        ""AlwaysMutedJson"" TEXT NULL,
                        ""AlwaysSoloJson"" TEXT NULL,
                        ""LastModified"" TEXT NOT NULL,
                        CONSTRAINT ""FK_StemPreferences_LibraryEntries_TrackUniqueHash"" FOREIGN KEY (""TrackUniqueHash"") REFERENCES ""LibraryEntries"" (""UniqueHash"") ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX ""IX_StemPreferences_TrackUniqueHash"" ON ""StemPreferences"" (""TrackUniqueHash"");
                ";
                await command.ExecuteNonQueryAsync();
                
                // Trigger migration from JSON file if it exists
                _ = Task.Run(() => MigrateStemPreferencesFromJsonAsync());
            }

            // Phase: Library Entry Enrichment Retry
            if (!ColumnExists("LibraryEntries", "EnrichmentAttempts"))
            {
                _logger.LogInformation("Patching Schema: Adding EnrichmentAttempts to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""EnrichmentAttempts"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "LastEnrichmentAttempt"))
            {
                _logger.LogInformation("Patching Schema: Adding LastEnrichmentAttempt to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""LastEnrichmentAttempt"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }

            // 1B. AudioFeatures Table (Phase 21: AI Brain)
            if (!TableExists("audio_features"))
            {
                _logger.LogInformation("Patching Schema: Creating AudioFeatures table (AI Brain)...");
                command.CommandText = @"
                    CREATE TABLE ""audio_features"" (
                        ""Id"" TEXT NOT NULL CONSTRAINT ""PK_audio_features"" PRIMARY KEY,
                        ""TrackUniqueHash"" TEXT NOT NULL UNIQUE,
                        ""TrackDuration"" REAL NOT NULL DEFAULT 0,

                        -- Core Musical Features
                        ""Bpm"" REAL NOT NULL DEFAULT 0,
                        ""BpmConfidence"" REAL NOT NULL DEFAULT 0,
                        ""Key"" TEXT NOT NULL DEFAULT '',
                        ""Scale"" TEXT NOT NULL DEFAULT '',
                        ""KeyConfidence"" REAL NOT NULL DEFAULT 0,
                        ""CamelotKey"" TEXT NOT NULL DEFAULT '',

                        -- Sonic Characteristics
                        ""Energy"" REAL NOT NULL DEFAULT 0,
                        ""Danceability"" REAL NOT NULL DEFAULT 0,
                        ""Intensity"" REAL NOT NULL DEFAULT 0,
                        ""SpectralCentroid"" REAL NOT NULL DEFAULT 0,
                        ""SpectralComplexity"" REAL NOT NULL DEFAULT 0,
                        ""OnsetRate"" REAL NOT NULL DEFAULT 0,
                        ""DynamicComplexity"" REAL NOT NULL DEFAULT 0,
                        ""LoudnessLUFS"" REAL NOT NULL DEFAULT 0,

                        -- Drop Detection & DJ Cues
                        ""DropTimeSeconds"" REAL NULL,
                        ""DropConfidence"" REAL NOT NULL DEFAULT 0,
                        ""CueIntro"" REAL NOT NULL DEFAULT 0,
                        ""CueBuild"" REAL NULL,
                        ""CueDrop"" REAL NULL,
                        ""CuePhraseStart"" REAL NULL,

                        -- Forensic Librarian
                        ""BpmStability"" REAL NOT NULL DEFAULT 1.0,
                        ""IsDynamicCompressed"" INTEGER NOT NULL DEFAULT 0,

                        -- AI Layer (Vibe & Vocals)
                        ""InstrumentalProbability"" REAL NOT NULL DEFAULT 0,
                        ""MoodTag"" TEXT NOT NULL DEFAULT '',
                        ""MoodConfidence"" REAL NOT NULL DEFAULT 0,
                        ""MusicBrainzId"" TEXT NOT NULL DEFAULT '',

                        -- EDM Specialist Models
                        ""Arousal"" REAL NOT NULL DEFAULT 5,
                        ""Valence"" REAL NOT NULL DEFAULT 5,
                        ""Sadness"" REAL NULL,
                        ""VectorEmbedding"" BLOB NULL,
                        ""ElectronicSubgenre"" TEXT NOT NULL DEFAULT '',
                        ""ElectronicSubgenreConfidence"" REAL NOT NULL DEFAULT 0,
                        ""IsDjTool"" INTEGER NOT NULL DEFAULT 0,
                        ""TonalProbability"" REAL NOT NULL DEFAULT 0.5,

                        -- Advanced Harmonic Mixing
                        ""ChordProgression"" TEXT NOT NULL DEFAULT '',

                        -- Identity & Metadata
                        ""Fingerprint"" TEXT NOT NULL DEFAULT '',
                        ""AnalysisVersion"" TEXT NOT NULL DEFAULT '',
                        ""AnalyzedAt"" TEXT NOT NULL,

                        -- Sonic Taxonomy (Style Lab)
                        ""DetectedSubGenre"" TEXT NOT NULL DEFAULT '',
                        ""SubGenreConfidence"" REAL NOT NULL DEFAULT 0,
                        ""GenreDistributionJson"" TEXT NOT NULL DEFAULT '{}',

                        -- ML.NET Brain
                        ""AiEmbeddingJson"" TEXT NOT NULL DEFAULT '',
                        ""PredictedVibe"" TEXT NOT NULL DEFAULT '',
                        ""PredictionConfidence"" REAL NOT NULL DEFAULT 0,
                        ""EmbeddingMagnitude"" REAL NOT NULL DEFAULT 0,

                        -- Provenance & Reliability
                        ""CurationConfidence"" INTEGER NOT NULL DEFAULT 0,
                        ""Source"" INTEGER NOT NULL DEFAULT 0,
                        ""ProvenanceJson"" TEXT NOT NULL DEFAULT '',

                        -- Phase 1 Foundations: Structural & curves
                        ""PhraseSegmentsJson"" TEXT NOT NULL DEFAULT '[]',
                        ""EnergyCurveJson"" TEXT NOT NULL DEFAULT '[]',
                        ""VocalDensityCurveJson"" TEXT NOT NULL DEFAULT '[]',
                        ""AnalysisReasoningJson"" TEXT NOT NULL DEFAULT '{}',

                        -- Phase 3.5: Vocal Intelligence
                        ""DetectedVocalType"" INTEGER NOT NULL DEFAULT 0,
                        ""VocalIntensity"" REAL NOT NULL DEFAULT 0,
                        ""VocalStartSeconds"" REAL NULL,
                        ""VocalEndSeconds"" REAL NULL
                    );
                    CREATE UNIQUE INDEX ""IX_audio_features_TrackUniqueHash"" ON ""audio_features"" (""TrackUniqueHash"");
                ";
                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("✅ AudioFeatures table created successfully");
            }

            // 1B-2. AudioAnalysis Table (Phase 21: Deep Learning Cortex)
            // Ensure VectorEmbeddingJson exists - Force Attempt
            try
            {
                _logger.LogInformation("Patching Schema: Checking/Adding VectorEmbeddingJson to audio_analysis...");
                command.CommandText = @"ALTER TABLE ""audio_analysis"" ADD COLUMN ""VectorEmbeddingJson"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("✅ Added VectorEmbeddingJson to audio_analysis");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column"))
            {
                _logger.LogInformation("VectorEmbeddingJson column already exists in audio_analysis, skipping");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such table"))
            {
                _logger.LogWarning("audio_analysis table missing! It should have been created by EF Core migrations.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to patch audio_analysis table");
            }

            if (!ColumnExists("audio_features", "MusicBrainzId"))
            {
                _logger.LogInformation("Patching Schema: Adding MusicBrainzId to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""MusicBrainzId"" TEXT NOT NULL DEFAULT '';";
                await command.ExecuteNonQueryAsync();
            }

            // Phase 1 Foundations: Structural & curves patches
            if (!ColumnExists("audio_features", "PhraseSegmentsJson"))
            {
                _logger.LogInformation("Patching Schema: Adding PhraseSegmentsJson to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""PhraseSegmentsJson"" TEXT NOT NULL DEFAULT '[]';";
                await command.ExecuteNonQueryAsync();
            }

            if (!ColumnExists("audio_features", "EnergyCurveJson"))
            {
                _logger.LogInformation("Patching Schema: Adding EnergyCurveJson to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""EnergyCurveJson"" TEXT NOT NULL DEFAULT '[]';";
                await command.ExecuteNonQueryAsync();
            }

            if (!ColumnExists("audio_features", "VocalDensityCurveJson"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalDensityCurveJson to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""VocalDensityCurveJson"" TEXT NOT NULL DEFAULT '[]';";
                await command.ExecuteNonQueryAsync();
            }

            if (!ColumnExists("audio_features", "AnalysisReasoningJson"))
            {
                _logger.LogInformation("Patching Schema: Adding AnalysisReasoningJson to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""AnalysisReasoningJson"" TEXT NOT NULL DEFAULT '{}';";
                await command.ExecuteNonQueryAsync();
            }

            if (!ColumnExists("audio_features", "AnomaliesJson"))
            {
                _logger.LogInformation("Patching Schema: Adding AnomaliesJson to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""AnomaliesJson"" TEXT NOT NULL DEFAULT '[]';";
                await command.ExecuteNonQueryAsync();
            }

            if (!ColumnExists("audio_features", "TrackDuration"))
            {
                _logger.LogInformation("Patching Schema: Adding TrackDuration to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""TrackDuration"" REAL NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }

            if (!ColumnExists("audio_features", "StructuralVersion"))
            {
                _logger.LogInformation("Patching Schema: Adding StructuralVersion to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""StructuralVersion"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }

            if (!ColumnExists("audio_features", "StructuralHash"))
            {
                _logger.LogInformation("Patching Schema: Adding StructuralHash to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""StructuralHash"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }

            // Phase 4: DetectedVocalType for Vocal Intelligence
            if (!ColumnExists("audio_features", "DetectedVocalType"))
            {
                _logger.LogInformation("Patching Schema: Adding DetectedVocalType to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""DetectedVocalType"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("audio_features", "VocalIntensity"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalIntensity to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""VocalIntensity"" REAL NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("audio_features", "VocalStartSeconds"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalStartSeconds to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""VocalStartSeconds"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("audio_features", "VocalEndSeconds"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalEndSeconds to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""VocalEndSeconds"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }

            // Phase 5: VocalDensity for advanced matching transparency
            if (!ColumnExists("audio_features", "VocalDensity"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalDensity to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""VocalDensity"" REAL NOT NULL DEFAULT 0.0;";
                await command.ExecuteNonQueryAsync();
            }

            // Phase 6: EnergyScore for DJ-style 1-10 energy rating
            if (!ColumnExists("audio_features", "EnergyScore"))
            {
                _logger.LogInformation("Patching Schema: Adding EnergyScore to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""EnergyScore"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }

            // Phase 6B: SegmentedEnergyJson for 8-point energy curve
            if (!ColumnExists("audio_features", "SegmentedEnergyJson"))
            {
                _logger.LogInformation("Patching Schema: Adding SegmentedEnergyJson to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""SegmentedEnergyJson"" TEXT NOT NULL DEFAULT '[]';";
                await command.ExecuteNonQueryAsync();
            }

            // Tier 3: Specialized Analysis columns
            if (!ColumnExists("audio_features", "AvgPitch"))
            {
                _logger.LogInformation("Patching Schema: Adding AvgPitch to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""AvgPitch"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("audio_features", "PitchConfidence"))
            {
                _logger.LogInformation("Patching Schema: Adding PitchConfidence to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""PitchConfidence"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("audio_features", "VggishEmbeddingJson"))
            {
                _logger.LogInformation("Patching Schema: Adding VggishEmbeddingJson to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""VggishEmbeddingJson"" TEXT NOT NULL DEFAULT '';";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("audio_features", "VisualizationVectorJson"))
            {
                _logger.LogInformation("Patching Schema: Adding VisualizationVectorJson to audio_features...");
                command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""VisualizationVectorJson"" TEXT NOT NULL DEFAULT '';";
                await command.ExecuteNonQueryAsync();
            }

            // 1C. AnalysisRuns Table (Phase 21: Analysis Run Tracking)
            if (!TableExists("analysis_runs"))
            {
                _logger.LogInformation("Patching Schema: Creating AnalysisRuns table (Run Tracking & Error Logging)...");
                command.CommandText = @"
                    CREATE TABLE ""analysis_runs"" (
                        ""RunId"" TEXT NOT NULL CONSTRAINT ""PK_analysis_runs"" PRIMARY KEY,
                        ""TrackUniqueHash"" TEXT NOT NULL,
                        ""TrackTitle"" TEXT NOT NULL DEFAULT '',
                        ""FilePath"" TEXT NOT NULL DEFAULT '',

                        -- Run Metadata
                        ""StartedAt"" TEXT NOT NULL,
                        ""CompletedAt"" TEXT NULL,
                        ""DurationMs"" INTEGER NOT NULL DEFAULT 0,

                        -- Status Tracking
                        ""Status"" INTEGER NOT NULL DEFAULT 0,
                        ""RetryAttempt"" INTEGER NOT NULL DEFAULT 0,
                        ""WorkerThreadId"" INTEGER NOT NULL DEFAULT 0,

                        -- Error Handling
                        ""ErrorMessage"" TEXT NULL,
                        ""ErrorStackTrace"" TEXT NULL,
                        ""FailedStage"" TEXT NULL,

                        -- Partial Success Tracking
                        ""WaveformGenerated"" INTEGER NOT NULL DEFAULT 0,
                        ""FfmpegAnalysisCompleted"" INTEGER NOT NULL DEFAULT 0,
                        ""EssentiaAnalysisCompleted"" INTEGER NOT NULL DEFAULT 0,
                        ""DatabaseSaved"" INTEGER NOT NULL DEFAULT 0,

                        -- Performance Metrics
                        ""FfmpegDurationMs"" INTEGER NOT NULL DEFAULT 0,
                        ""EssentiaDurationMs"" INTEGER NOT NULL DEFAULT 0,
                        ""DatabaseSaveDurationMs"" INTEGER NOT NULL DEFAULT 0,

                        -- Provenance
                        ""AnalysisVersion"" TEXT NOT NULL DEFAULT '',
                        ""TriggerSource"" TEXT NOT NULL DEFAULT '',
                        ""Tier"" INTEGER NOT NULL DEFAULT 1
                    );
                    CREATE INDEX ""IX_analysis_runs_TrackUniqueHash"" ON ""analysis_runs"" (""TrackUniqueHash"");
                    CREATE INDEX ""IX_analysis_runs_Status"" ON ""analysis_runs"" (""Status"");
                    CREATE INDEX ""IX_analysis_runs_StartedAt"" ON ""analysis_runs"" (""StartedAt"");
                ";
                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("✅ AnalysisRuns table created successfully");
            }
            else
            {
                // Patch existing table
                if (!ColumnExists("analysis_runs", "Tier"))
                {
                    _logger.LogInformation("Patching Schema: Adding Tier to AnalysisRuns...");
                    command.CommandText = @"ALTER TABLE ""analysis_runs"" ADD COLUMN ""Tier"" INTEGER NOT NULL DEFAULT 1;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("analysis_runs", "AnalysisVersion"))
                {
                    _logger.LogInformation("Patching Schema: Adding AnalysisVersion to AnalysisRuns...");
                    command.CommandText = @"ALTER TABLE ""analysis_runs"" ADD COLUMN ""AnalysisVersion"" TEXT NOT NULL DEFAULT '';";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("analysis_runs", "TriggerSource"))
                {
                    _logger.LogInformation("Patching Schema: Adding TriggerSource to AnalysisRuns...");
                    command.CommandText = @"ALTER TABLE ""analysis_runs"" ADD COLUMN ""TriggerSource"" TEXT NOT NULL DEFAULT '';";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("analysis_runs", "BpmConfidence"))
                {
                    _logger.LogInformation("Patching Schema: Adding BpmConfidence to AnalysisRuns...");
                    command.CommandText = @"ALTER TABLE ""analysis_runs"" ADD COLUMN ""BpmConfidence"" REAL NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("analysis_runs", "KeyConfidence"))
                {
                    _logger.LogInformation("Patching Schema: Adding KeyConfidence to AnalysisRuns...");
                    command.CommandText = @"ALTER TABLE ""analysis_runs"" ADD COLUMN ""KeyConfidence"" REAL NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("analysis_runs", "IntegrityScore"))
                {
                    _logger.LogInformation("Patching Schema: Adding IntegrityScore to AnalysisRuns...");
                    command.CommandText = @"ALTER TABLE ""analysis_runs"" ADD COLUMN ""IntegrityScore"" REAL NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("analysis_runs", "CurrentStage"))
                {
                    _logger.LogInformation("Patching Schema: Adding CurrentStage to AnalysisRuns...");
                    command.CommandText = @"ALTER TABLE ""analysis_runs"" ADD COLUMN ""CurrentStage"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
            }

            // 1D. Tracks Table
            if (TableExists("Tracks"))
            {
                if (!ColumnExists("Tracks", "Label"))
                {
                    _logger.LogInformation("Patching Schema: Adding Label to Tracks...");
                    command.CommandText = @"ALTER TABLE ""Tracks"" ADD COLUMN ""Label"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("Tracks", "Comments"))
                {
                    _logger.LogInformation("Patching Schema: Adding Comments to Tracks...");
                    command.CommandText = @"ALTER TABLE ""Tracks"" ADD COLUMN ""Comments"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("Tracks", "DropTimestamp"))
                {
                    _logger.LogInformation("Patching Schema: Adding DropTimestamp to Tracks...");
                    command.CommandText = @"ALTER TABLE ""Tracks"" ADD COLUMN ""DropTimestamp"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("Tracks", "ManualEnergy"))
                {
                    _logger.LogInformation("Patching Schema: Adding ManualEnergy to Tracks...");
                    command.CommandText = @"ALTER TABLE ""Tracks"" ADD COLUMN ""ManualEnergy"" INTEGER NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("Tracks", "SourceProvenance"))
                {
                    _logger.LogInformation("Patching Schema: Adding SourceProvenance to Tracks...");
                    command.CommandText = @"ALTER TABLE ""Tracks"" ADD COLUMN ""SourceProvenance"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("Tracks", "MusicBrainzId"))
                {
                    _logger.LogInformation("Patching Schema: Adding MusicBrainzId to Tracks...");
                    command.CommandText = @"ALTER TABLE ""Tracks"" ADD COLUMN ""MusicBrainzId"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("Tracks", "MoodTag"))
                {
                    _logger.LogInformation("Patching Schema: Adding MoodTag to Tracks...");
                    command.CommandText = @"ALTER TABLE ""Tracks"" ADD COLUMN ""MoodTag"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
            }

            // 2. PlaylistTracks Columns
            if (!ColumnExists("PlaylistTracks", "PrimaryGenre"))
            {
                _logger.LogInformation("Patching Schema: Adding PrimaryGenre to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""PrimaryGenre"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "VocalType"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalType to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""VocalType"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "VocalIntensity"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalIntensity to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""VocalIntensity"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "VocalStartSeconds"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalStartSeconds to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""VocalStartSeconds"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "VocalEndSeconds"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalEndSeconds to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""VocalEndSeconds"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "Label"))
            {
                _logger.LogInformation("Patching Schema: Adding Label to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""Label"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "Comments"))
            {
                _logger.LogInformation("Patching Schema: Adding Comments to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""Comments"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "IsPrepared"))
            {
                _logger.LogInformation("Patching Schema: Adding IsPrepared to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""IsPrepared"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "DropTimestamp"))
            {
                _logger.LogInformation("Patching Schema: Adding DropTimestamp to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""DropTimestamp"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "ManualEnergy"))
            {
                _logger.LogInformation("Patching Schema: Adding ManualEnergy to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""ManualEnergy"" INTEGER NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "SourceProvenance"))
            {
                _logger.LogInformation("Patching Schema: Adding SourceProvenance to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""SourceProvenance"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "MusicBrainzId"))
            {
                _logger.LogInformation("Patching Schema: Adding MusicBrainzId to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""MusicBrainzId"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("PlaylistTracks", "MoodTag"))
            {
                _logger.LogInformation("Patching Schema: Adding MoodTag to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""MoodTag"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }

            if (!ColumnExists("PlaylistTracks", "IsUserPaused"))
            {
                _logger.LogInformation("Patching Schema: Adding IsUserPaused to PlaylistTracks...");
                command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""IsUserPaused"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }

            // 3. LibraryEntries Columns
            if (!ColumnExists("LibraryEntries", "PrimaryGenre"))
            {
                _logger.LogInformation("Patching Schema: Adding PrimaryGenre to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""PrimaryGenre"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "Label"))
            {
                _logger.LogInformation("Patching Schema: Adding Label to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""Label"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "Comments"))
            {
                _logger.LogInformation("Patching Schema: Adding Comments to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""Comments"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "QualityDetails"))
            {
                _logger.LogInformation("Patching Schema: Adding QualityDetails to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""QualityDetails"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "SpectralHash"))
            {
                _logger.LogInformation("Patching Schema: Adding SpectralHash to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""SpectralHash"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "VocalType"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalType to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""VocalType"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "VocalIntensity"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalIntensity to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""VocalIntensity"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "VocalStartSeconds"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalStartSeconds to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""VocalStartSeconds"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "VocalEndSeconds"))
            {
                _logger.LogInformation("Patching Schema: Adding VocalEndSeconds to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""VocalEndSeconds"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "WaveformData"))
            {
                _logger.LogInformation("Patching Schema: Adding WaveformData to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""WaveformData"" BLOB NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "RmsData"))
            {
                _logger.LogInformation("Patching Schema: Adding RmsData to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""RmsData"" BLOB NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "LowData"))
            {
                _logger.LogInformation("Patching Schema: Adding LowData to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""LowData"" BLOB NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "MidData"))
            {
                _logger.LogInformation("Patching Schema: Adding MidData to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""MidData"" BLOB NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "HighData"))
            {
                _logger.LogInformation("Patching Schema: Adding HighData to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""HighData"" BLOB NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "IsPrepared"))
            {
                _logger.LogInformation("Patching Schema: Adding IsPrepared to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""IsPrepared"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "CuePointsJson"))
            {
                _logger.LogInformation("Patching Schema: Adding CuePointsJson to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""CuePointsJson"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "DropTimestamp"))
            {
                _logger.LogInformation("Patching Schema: Adding DropTimestamp to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""DropTimestamp"" REAL NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "ManualEnergy"))
            {
                _logger.LogInformation("Patching Schema: Adding ManualEnergy to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""ManualEnergy"" INTEGER NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "SourceProvenance"))
            {
                _logger.LogInformation("Patching Schema: Adding SourceProvenance to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""SourceProvenance"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "MusicBrainzId"))
            {
                _logger.LogInformation("Patching Schema: Adding MusicBrainzId to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""MusicBrainzId"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "MoodTag"))
            {
                _logger.LogInformation("Patching Schema: Adding MoodTag to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""MoodTag"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "Rating"))
            {
                _logger.LogInformation("Patching Schema: Adding Rating to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""Rating"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "IsLiked"))
            {
                _logger.LogInformation("Patching Schema: Adding IsLiked to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""IsLiked"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "PlayCount"))
            {
                _logger.LogInformation("Patching Schema: Adding PlayCount to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""PlayCount"" INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }
            if (!ColumnExists("LibraryEntries", "LastPlayedAt"))
            {
                _logger.LogInformation("Patching Schema: Adding LastPlayedAt to LibraryEntries...");
                command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""LastPlayedAt"" TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }

            // 4. TechnicalDetails Table Columns (for existing tables)
            if (TableExists("TechnicalDetails"))
            {
                if (!ColumnExists("TechnicalDetails", "IsPrepared"))
                {
                    _logger.LogInformation("Patching Schema: Adding IsPrepared to TechnicalDetails...");
                    command.CommandText = @"ALTER TABLE ""TechnicalDetails"" ADD COLUMN ""IsPrepared"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("TechnicalDetails", "CurationConfidence"))
                {
                    _logger.LogInformation("Patching Schema: Adding CurationConfidence to TechnicalDetails...");
                    command.CommandText = @"ALTER TABLE ""TechnicalDetails"" ADD COLUMN ""CurationConfidence"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("TechnicalDetails", "ProvenanceJson"))
                {
                    _logger.LogInformation("Patching Schema: Adding ProvenanceJson to TechnicalDetails...");
                    command.CommandText = @"ALTER TABLE ""TechnicalDetails"" ADD COLUMN ""ProvenanceJson"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("TechnicalDetails", "IsReviewNeeded"))
                {
                    _logger.LogInformation("Patching Schema: Adding IsReviewNeeded to TechnicalDetails...");
                    command.CommandText = @"ALTER TABLE ""TechnicalDetails"" ADD COLUMN ""IsReviewNeeded"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("TechnicalDetails", "PrimaryGenre"))
                {
                    _logger.LogInformation("Patching Schema: Adding PrimaryGenre to TechnicalDetails...");
                    command.CommandText = @"ALTER TABLE ""TechnicalDetails"" ADD COLUMN ""PrimaryGenre"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
            }

            // 5. AudioFeatures Table Columns - Force attempt (table may not exist yet during cold start)
            try
            {
                _logger.LogInformation("Attempting to add missing columns to audio_features...");
                
                // AiEmbeddingJson
                try {
                    command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""AiEmbeddingJson"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                    _logger.LogInformation("✅ AiEmbeddingJson column added to audio_features");
                } catch { }

                // CuePointsJson [Phase 17/Sprint 5 Fix]
                try {
                    command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""CuePointsJson"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                    _logger.LogInformation("✅ CuePointsJson column added to audio_features");
                } catch { }
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such table"))
            {
                _logger.LogInformation("AudioFeatures table doesn't exist yet, skipping (will be created with column)");
            }

            // 6. Phase 17: EDM Specialist Columns for AudioFeatures
            // Using force-add pattern - try to add and catch duplicate errors
            _logger.LogInformation("Phase 17: Checking EDM specialist columns for AudioFeatures...");

            var edmColumns = new[]
            {
                ("Arousal", "REAL NOT NULL DEFAULT 5"),
                ("Valence", "REAL NOT NULL DEFAULT 5"),
                ("Sadness", "REAL NULL"),
                ("ElectronicSubgenre", "TEXT NULL DEFAULT ''"),
                ("ElectronicSubgenreConfidence", "REAL NOT NULL DEFAULT 0"),
                ("IsDjTool", "INTEGER NOT NULL DEFAULT 0"),
                ("TonalProbability", "REAL NOT NULL DEFAULT 0.5"),
                ("Intensity", "REAL NOT NULL DEFAULT 0"),
                ("AvgPitch", "REAL NULL"),
                ("PitchConfidence", "REAL NULL"),
                ("VggishEmbeddingJson", "TEXT NULL DEFAULT ''"),
                ("VisualizationVectorJson", "TEXT NULL DEFAULT ''")
            };

            foreach (var (columnName, columnDef) in edmColumns)
            {
                try
                {
                    command.CommandText = $@"ALTER TABLE ""audio_features"" ADD COLUMN ""{columnName}"" {columnDef};";
                    await command.ExecuteNonQueryAsync();
                    _logger.LogInformation("✅ Added column {Column} to audio_features", columnName);
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column"))
                {
                    // Column already exists, skip silently
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such table"))
                {
                    _logger.LogWarning("audio_features table doesn't exist yet, will be created by EF Core");
                    break; // No point continuing if table doesn't exist
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to add column {Column} to audio_features", columnName);
                }
            }

            // 7. Phase 17: TrackPhrases Table
            if (!TableExists("TrackPhrases"))
            {
                _logger.LogInformation("Patching Schema: Creating TrackPhrases table...");
                command.CommandText = @"
                    CREATE TABLE ""TrackPhrases"" (
                        ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                        ""TrackUniqueHash"" TEXT NOT NULL,
                        ""Type"" INTEGER NOT NULL,
                        ""StartTimeSeconds"" REAL NOT NULL,
                        ""EndTimeSeconds"" REAL NOT NULL,
                        ""EnergyLevel"" REAL NOT NULL DEFAULT 0,
                        ""Confidence"" REAL NOT NULL DEFAULT 0,
                        ""OrderIndex"" INTEGER NOT NULL DEFAULT 0,
                        ""Label"" TEXT NULL
                    );
                    CREATE INDEX ""IX_TrackPhrases_TrackUniqueHash"" ON ""TrackPhrases"" (""TrackUniqueHash"");
                ";
                await command.ExecuteNonQueryAsync();
            }

            // 8. Phase 17: GenreCueTemplates Table
            if (!TableExists("GenreCueTemplates"))
            {
                _logger.LogInformation("Patching Schema: Creating GenreCueTemplates table...");
                command.CommandText = @"
                    CREATE TABLE ""GenreCueTemplates"" (
                        ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                        ""GenreName"" TEXT NOT NULL,
                        ""DisplayName"" TEXT NULL,
                        ""IsBuiltIn"" INTEGER NOT NULL DEFAULT 0,
                        ""Cue1Target"" INTEGER NOT NULL DEFAULT 0,
                        ""Cue1OffsetBars"" INTEGER NOT NULL DEFAULT 0,
                        ""Cue1Color"" TEXT NOT NULL DEFAULT '#FF0000',
                        ""Cue1Label"" TEXT NULL,
                        ""Cue2Target"" INTEGER NOT NULL DEFAULT 0,
                        ""Cue2OffsetBars"" INTEGER NOT NULL DEFAULT 0,
                        ""Cue2Color"" TEXT NOT NULL DEFAULT '#00FF00',
                        ""Cue2Label"" TEXT NULL,
                        ""Cue3Target"" INTEGER NOT NULL DEFAULT 0,
                        ""Cue3OffsetBars"" INTEGER NOT NULL DEFAULT 0,
                        ""Cue3Color"" TEXT NOT NULL DEFAULT '#0000FF',
                        ""Cue3Label"" TEXT NULL,
                        ""Cue4Target"" INTEGER NOT NULL DEFAULT 0,
                        ""Cue4OffsetBars"" INTEGER NOT NULL DEFAULT 0,
                        ""Cue4Color"" TEXT NOT NULL DEFAULT '#FFFF00',
                        ""Cue4Label"" TEXT NULL,
                        ""Cue5Target"" INTEGER NULL,
                        ""Cue5OffsetBars"" INTEGER NULL,
                        ""Cue5Color"" TEXT NULL,
                        ""Cue5Label"" TEXT NULL,
                        ""Cue6Target"" INTEGER NULL,
                        ""Cue6OffsetBars"" INTEGER NULL,
                        ""Cue6Color"" TEXT NULL,
                        ""Cue6Label"" TEXT NULL,
                        ""Cue7Target"" INTEGER NULL,
                        ""Cue7OffsetBars"" INTEGER NULL,
                        ""Cue7Color"" TEXT NULL,
                        ""Cue7Label"" TEXT NULL,
                        ""Cue8Target"" INTEGER NULL,
                        ""Cue8OffsetBars"" INTEGER NULL,
                        ""Cue8Color"" TEXT NULL,
                        ""Cue8Label"" TEXT NULL
                    );
                    CREATE INDEX ""IX_GenreCueTemplates_GenreName"" ON ""GenreCueTemplates"" (""GenreName"");
                ";
                await command.ExecuteNonQueryAsync();
            }

            // 9. Phase 20: Smart Playlists (Projects Table)
            if (TableExists("Projects"))
            {
                if (!ColumnExists("Projects", "IsSmartPlaylist"))
                {
                    _logger.LogInformation("Patching Schema: Adding IsSmartPlaylist to Projects...");
                    command.CommandText = @"ALTER TABLE ""Projects"" ADD COLUMN ""IsSmartPlaylist"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("Projects", "SmartCriteriaJson"))
                {
                    _logger.LogInformation("Patching Schema: Adding SmartCriteriaJson to Projects...");
                    command.CommandText = @"ALTER TABLE ""Projects"" ADD COLUMN ""SmartCriteriaJson"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("Projects", "AnalysisStatus"))
                {
                    _logger.LogInformation("Patching Schema: Adding AnalysisStatus to Projects...");
                    command.CommandText = @"ALTER TABLE ""Projects"" ADD COLUMN ""AnalysisStatus"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
            }

            // 10. Phase 18.2: Sonic Visualizations
            if (TableExists("PlaylistTracks"))
            {
                if (!ColumnExists("PlaylistTracks", "InstrumentalProbability"))
                {
                    _logger.LogInformation("Patching Schema: Adding InstrumentalProbability to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""InstrumentalProbability"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("PlaylistTracks", "Arousal"))
                {
                    _logger.LogInformation("Patching Schema: Adding Arousal to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""Arousal"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
            }
            if (TableExists("LibraryEntries"))
            {
                if (!ColumnExists("LibraryEntries", "InstrumentalProbability"))
                {
                    _logger.LogInformation("Patching Schema: Adding InstrumentalProbability to LibraryEntries...");
                    command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""InstrumentalProbability"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("LibraryEntries", "Arousal"))
                {
                    _logger.LogInformation("Patching Schema: Adding Arousal to LibraryEntries...");
                    command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""Arousal"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
            }
            if (TableExists("audio_features"))
            {
                try
                {
                    if (!ColumnExists("audio_features", "InstrumentalProbability"))
                    {
                        _logger.LogInformation("Patching Schema: Adding InstrumentalProbability to audio_features...");
                        command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""InstrumentalProbability"" REAL NULL;";
                        await command.ExecuteNonQueryAsync();
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to patch InstrumentalProbability"); }

                try
                {
                    if (!ColumnExists("audio_features", "Arousal"))
                    {
                        _logger.LogInformation("Patching Schema: Adding Arousal to audio_features...");
                        command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""Arousal"" REAL NULL;";
                        await command.ExecuteNonQueryAsync();
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to patch Arousal"); }

                try
                {
                    if (!ColumnExists("audio_features", "Sadness"))
                    {
                        _logger.LogInformation("Patching Schema: Adding Sadness to audio_features...");
                        command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""Sadness"" REAL NULL;";
                        await command.ExecuteNonQueryAsync();
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to patch Sadness"); }

                try
                {
                    if (!ColumnExists("audio_features", "CamelotKey"))
                    {
                        _logger.LogInformation("Patching Schema: Adding CamelotKey to audio_features...");
                        command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""CamelotKey"" TEXT NOT NULL DEFAULT '';";
                        await command.ExecuteNonQueryAsync();
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to patch CamelotKey"); }
            }

            // 11. Phase 21: Smart Enrichment Retry System
            if (TableExists("PlaylistTracks"))
            {
                if (!ColumnExists("PlaylistTracks", "LastEnrichmentAttempt"))
                {
                    _logger.LogInformation("Patching Schema: Adding LastEnrichmentAttempt to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""LastEnrichmentAttempt"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("PlaylistTracks", "Rating"))
                {
                    _logger.LogInformation("Patching Schema: Adding Rating to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""Rating"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("PlaylistTracks", "IsLiked"))
                {
                    _logger.LogInformation("Patching Schema: Adding IsLiked to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""IsLiked"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("PlaylistTracks", "PlayCount"))
                {
                    _logger.LogInformation("Patching Schema: Adding PlayCount to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""PlayCount"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("PlaylistTracks", "LastPlayedAt"))
                {
                    _logger.LogInformation("Patching Schema: Adding LastPlayedAt to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""LastPlayedAt"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("PlaylistTracks", "Loudness"))
                {
                    _logger.LogInformation("Patching Schema: Adding Loudness to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""Loudness"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("PlaylistTracks", "TruePeak"))
                {
                    _logger.LogInformation("Patching Schema: Adding TruePeak to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""TruePeak"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("PlaylistTracks", "DynamicRange"))
                {
                    _logger.LogInformation("Patching Schema: Adding DynamicRange to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""DynamicRange"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("PlaylistTracks", "EnrichmentAttempts"))
                {
                    _logger.LogInformation("Patching Schema: Adding EnrichmentAttempts to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""EnrichmentAttempts"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("PlaylistTracks", "AnalysisStatus"))
                {
                    _logger.LogInformation("Patching Schema: Adding AnalysisStatus to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""AnalysisStatus"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
            }
            if (TableExists("LibraryEntries"))
            {
                if (!ColumnExists("LibraryEntries", "LastEnrichmentAttempt"))
                {
                    _logger.LogInformation("Patching Schema: Adding LastEnrichmentAttempt to LibraryEntries...");
                    command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""LastEnrichmentAttempt"" TEXT NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("LibraryEntries", "EnrichmentAttempts"))
                {
                    _logger.LogInformation("Patching Schema: Adding EnrichmentAttempts to LibraryEntries...");
                    command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""EnrichmentAttempts"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("LibraryEntries", "Loudness"))
                {
                    _logger.LogInformation("Patching Schema: Adding Loudness to LibraryEntries...");
                    command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""Loudness"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("LibraryEntries", "TruePeak"))
                {
                    _logger.LogInformation("Patching Schema: Adding TruePeak to LibraryEntries...");
                    command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""TruePeak"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("LibraryEntries", "DynamicRange"))
                {
                    _logger.LogInformation("Patching Schema: Adding DynamicRange to LibraryEntries...");
                    command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""DynamicRange"" REAL NULL;";
                    await command.ExecuteNonQueryAsync();
                }
                if (!ColumnExists("LibraryEntries", "AnalysisStatus"))
                {
                    _logger.LogInformation("Patching Schema: Adding AnalysisStatus to LibraryEntries...");
                    command.CommandText = @"ALTER TABLE ""LibraryEntries"" ADD COLUMN ""AnalysisStatus"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
            }

            // 12. Phase 23: Smart Crates
            if (!TableExists("smart_crate_definitions"))
            {
                _logger.LogInformation("Patching Schema: Creating smart_crate_definitions table...");
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ""smart_crate_definitions"" (
                        ""Id"" TEXT PRIMARY KEY,
                        ""Name"" TEXT NOT NULL,
                        ""RulesJson"" TEXT NOT NULL,
                        ""SortOrder"" INTEGER NOT NULL,
                        ""CreatedAt"" TEXT NOT NULL,
                        ""UpdatedAt"" TEXT NOT NULL
                    );";
                await command.ExecuteNonQueryAsync();
            }

            // 13. Phase 0: FTS5 Virtual Table for Instant Search
            // Fix: Reliance on rowid is fragile (e.g. after VACUUM).
            // We now store the GlobalId as an unindexed column for stable linking.
            bool isIncorrectlyConfigured = false;
            if (TableExists("TracksFts"))
            {
                using var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT sql FROM sqlite_master WHERE name='TracksFts'";
                var sql = (await checkCmd.ExecuteScalarAsync())?.ToString();

                // Drop if it's missing the GlobalId column or the new Key column
                if (sql != null && (!sql.Contains("GlobalId") || !sql.Contains(", Key")))
                {
                    isIncorrectlyConfigured = true;
                    _logger.LogWarning("TracksFts is incorrectly configured. Dropping and recreating...");
                    command.CommandText = "DROP TABLE TracksFts;";
                    await command.ExecuteNonQueryAsync();
                }
            }

            if (!TableExists("TracksFts") || isIncorrectlyConfigured)
            {
                _logger.LogInformation("Patching Schema: Creating robust FTS5 Virtual Table (TracksFts)...");
                command.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS TracksFts USING fts5(Artist, Title, Key, GlobalId UNINDEXED);";
                await command.ExecuteNonQueryAsync();
            }

            _logger.LogInformation("Patching Schema: Ensuring TracksFts triggers are correct...");
            command.CommandText = @"
                DROP TRIGGER IF EXISTS tbl_tracks_ai;
                CREATE TRIGGER tbl_tracks_ai AFTER INSERT ON Tracks BEGIN
                    INSERT INTO TracksFts(Artist, Title, Key, GlobalId) VALUES (
                        new.Artist,
                        new.Title,
                        COALESCE(new.MusicalKey, '') || ' ' || (CASE new.MusicalKey
                            WHEN 'Abm' THEN '1A' WHEN 'G#m' THEN '1A' WHEN 'B' THEN '1B' WHEN 'Cb' THEN '1B'
                            WHEN 'Ebm' THEN '2A' WHEN 'D#m' THEN '2A' WHEN 'F#' THEN '2B' WHEN 'Gb' THEN '2B'
                            WHEN 'Bbm' THEN '3A' WHEN 'A#m' THEN '3A' WHEN 'Db' THEN '3B' WHEN 'C#' THEN '3B'
                            WHEN 'Fm' THEN '4A' WHEN 'Ab' THEN '4B' WHEN 'G#' THEN '4B'
                            WHEN 'Cm' THEN '5A' WHEN 'Eb' THEN '5B' WHEN 'D#' THEN '5B'
                            WHEN 'Gm' THEN '6A' WHEN 'Bb' THEN '6B' WHEN 'A#' THEN '6B'
                            WHEN 'Dm' THEN '7A' WHEN 'F' THEN '7B'
                            WHEN 'Am' THEN '8A' WHEN 'C' THEN '8B'
                            WHEN 'Em' THEN '9A' WHEN 'G' THEN '9B'
                            WHEN 'Bm' THEN '10A' WHEN 'D' THEN '10B'
                            WHEN 'F#m' THEN '11A' WHEN 'Gbm' THEN '11A' WHEN 'A' THEN '11B'
                            WHEN 'Dbm' THEN '12A' WHEN 'C#m' THEN '12A' WHEN 'E' THEN '12B'
                            ELSE ''
                        END),
                        new.GlobalId
                    );
                END;

                DROP TRIGGER IF EXISTS tbl_tracks_ad;
                CREATE TRIGGER tbl_tracks_ad AFTER DELETE ON Tracks BEGIN
                    DELETE FROM TracksFts WHERE GlobalId = old.GlobalId;
                END;

                DROP TRIGGER IF EXISTS tbl_tracks_au;
                CREATE TRIGGER tbl_tracks_au AFTER UPDATE ON Tracks BEGIN
                    DELETE FROM TracksFts WHERE GlobalId = old.GlobalId;
                    INSERT INTO TracksFts(Artist, Title, Key, GlobalId) VALUES (
                        new.Artist,
                        new.Title,
                        COALESCE(new.MusicalKey, '') || ' ' || (CASE new.MusicalKey
                            WHEN 'Abm' THEN '1A' WHEN 'G#m' THEN '1A' WHEN 'B' THEN '1B' WHEN 'Cb' THEN '1B'
                            WHEN 'Ebm' THEN '2A' WHEN 'D#m' THEN '2A' WHEN 'F#' THEN '2B' WHEN 'Gb' THEN '2B'
                            WHEN 'Bbm' THEN '3A' WHEN 'A#m' THEN '3A' WHEN 'Db' THEN '3B' WHEN 'C#' THEN '3B'
                            WHEN 'Fm' THEN '4A' WHEN 'Ab' THEN '4B' WHEN 'G#' THEN '4B'
                            WHEN 'Cm' THEN '5A' WHEN 'Eb' THEN '5B' WHEN 'D#' THEN '5B'
                            WHEN 'Gm' THEN '6A' WHEN 'Bb' THEN '6B' WHEN 'A#' THEN '6B'
                            WHEN 'Dm' THEN '7A' WHEN 'F' THEN '7B'
                            WHEN 'Am' THEN '8A' WHEN 'C' THEN '8B'
                            WHEN 'Em' THEN '9A' WHEN 'G' THEN '9B'
                            WHEN 'Bm' THEN '10A' WHEN 'D' THEN '10B'
                            WHEN 'F#m' THEN '11A' WHEN 'Gbm' THEN '11A' WHEN 'A' THEN '11B'
                            WHEN 'Dbm' THEN '12A' WHEN 'C#m' THEN '12A' WHEN 'E' THEN '12B'
                            ELSE ''
                        END),
                        new.GlobalId
                    );
                END;
            ";
            await command.ExecuteNonQueryAsync();

            // Seed initial data if table is empty
            command.CommandText = "SELECT COUNT(*) FROM TracksFts";
            var ftsCount = Convert.ToInt64(await command.ExecuteScalarAsync());
            if (ftsCount == 0 || isIncorrectlyConfigured)
            {
                _logger.LogInformation("Seeding FTS5 index from existing tracks...");
                command.CommandText = @"
                    INSERT INTO TracksFts(Artist, Title, Key, GlobalId)
                    SELECT Artist, Title,
                           COALESCE(MusicalKey, '') || ' ' || (CASE MusicalKey
                            WHEN 'Abm' THEN '1A' WHEN 'G#m' THEN '1A' WHEN 'B' THEN '1B' WHEN 'Cb' THEN '1B'
                            WHEN 'Ebm' THEN '2A' WHEN 'D#m' THEN '2A' WHEN 'F#' THEN '2B' WHEN 'Gb' THEN '2B'
                            WHEN 'Bbm' THEN '3A' WHEN 'A#m' THEN '3A' WHEN 'Db' THEN '3B' WHEN 'C#' THEN '3B'
                            WHEN 'Fm' THEN '4A' WHEN 'Ab' THEN '4B' WHEN 'G#' THEN '4B'
                            WHEN 'Cm' THEN '5A' WHEN 'Eb' THEN '5B' WHEN 'D#' THEN '5B'
                            WHEN 'Gm' THEN '6A' WHEN 'Bb' THEN '6B' WHEN 'A#' THEN '6B'
                            WHEN 'Dm' THEN '7A' WHEN 'F' THEN '7B'
                            WHEN 'Am' THEN '8A' WHEN 'C' THEN '8B'
                            WHEN 'Em' THEN '9A' WHEN 'G' THEN '9B'
                            WHEN 'Bm' THEN '10A' WHEN 'D' THEN '10B'
                            WHEN 'F#m' THEN '11A' WHEN 'Gbm' THEN '11A' WHEN 'A' THEN '11B'
                            WHEN 'Dbm' THEN '12A' WHEN 'C#m' THEN '12A' WHEN 'E' THEN '12B'
                            ELSE ''
                        END),
                        GlobalId FROM Tracks;";
                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("✅ FTS5 search index seeded successfully.");
            }

            // 14. Phase 0: FTS5 Virtual Table for LibraryEntries (Main Library)
            bool isLibFtsIncorrect = false;
            if (TableExists("LibraryEntriesFts"))
            {
                using var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT sql FROM sqlite_master WHERE name='LibraryEntriesFts'";
                var sql = (await checkCmd.ExecuteScalarAsync())?.ToString();

                // Drop if it's missing the UniqueHash column or the new Key column or has legacy content mapping
                if (sql != null && (!sql.Contains("UniqueHash") || !sql.Contains(", Key") || sql.Contains("content=")))
                {
                    isLibFtsIncorrect = true;
                    _logger.LogWarning("LibraryEntriesFts is incorrectly configured. Dropping and recreating...");
                    command.CommandText = "DROP TABLE LibraryEntriesFts;";
                    await command.ExecuteNonQueryAsync();
                }
            }

            if (!TableExists("LibraryEntriesFts") || isLibFtsIncorrect)
            {
                _logger.LogInformation("Patching Schema: Creating robust FTS5 Virtual Table (LibraryEntriesFts)...");
                command.CommandText = @"
                    CREATE VIRTUAL TABLE IF NOT EXISTS LibraryEntriesFts USING fts5(
                        Artist,
                        Title,
                        Album,
                        Key,
                        UniqueHash UNINDEXED
                    );";
                await command.ExecuteNonQueryAsync();
            }

            _logger.LogInformation("Patching Schema: Ensuring LibraryEntriesFts triggers are correct...");
            command.CommandText = @"
                DROP TRIGGER IF EXISTS tbl_lib_ai;
                CREATE TRIGGER tbl_lib_ai AFTER INSERT ON LibraryEntries BEGIN
                    INSERT INTO LibraryEntriesFts(Artist, Title, Album, Key, UniqueHash) VALUES (
                        new.Artist,
                        new.Title,
                        new.Album,
                        COALESCE(new.MusicalKey, '') || ' ' || (CASE new.MusicalKey
                            WHEN 'Abm' THEN '1A' WHEN 'G#m' THEN '1A' WHEN 'B' THEN '1B' WHEN 'Cb' THEN '1B'
                            WHEN 'Ebm' THEN '2A' WHEN 'D#m' THEN '2A' WHEN 'F#' THEN '2B' WHEN 'Gb' THEN '2B'
                            WHEN 'Bbm' THEN '3A' WHEN 'A#m' THEN '3A' WHEN 'Db' THEN '3B' WHEN 'C#' THEN '3B'
                            WHEN 'Fm' THEN '4A' WHEN 'Ab' THEN '4B' WHEN 'G#' THEN '4B'
                            WHEN 'Cm' THEN '5A' WHEN 'Eb' THEN '5B' WHEN 'D#' THEN '5B'
                            WHEN 'Gm' THEN '6A' WHEN 'Bb' THEN '6B' WHEN 'A#' THEN '6B'
                            WHEN 'Dm' THEN '7A' WHEN 'F' THEN '7B'
                            WHEN 'Am' THEN '8A' WHEN 'C' THEN '8B'
                            WHEN 'Em' THEN '9A' WHEN 'G' THEN '9B'
                            WHEN 'Bm' THEN '10A' WHEN 'D' THEN '10B'
                            WHEN 'F#m' THEN '11A' WHEN 'Gbm' THEN '11A' WHEN 'A' THEN '11B'
                            WHEN 'Dbm' THEN '12A' WHEN 'C#m' THEN '12A' WHEN 'E' THEN '12B'
                            ELSE ''
                        END),
                        new.UniqueHash
                    );
                END;

                DROP TRIGGER IF EXISTS tbl_lib_ad;
                CREATE TRIGGER tbl_lib_ad AFTER DELETE ON LibraryEntries BEGIN
                    DELETE FROM LibraryEntriesFts WHERE UniqueHash = old.UniqueHash;
                END;

                DROP TRIGGER IF EXISTS tbl_lib_au;
                CREATE TRIGGER tbl_lib_au AFTER UPDATE ON LibraryEntries BEGIN
                    DELETE FROM LibraryEntriesFts WHERE UniqueHash = old.UniqueHash;
                    INSERT INTO LibraryEntriesFts(Artist, Title, Album, Key, UniqueHash) VALUES (
                        new.Artist,
                        new.Title,
                        new.Album,
                        COALESCE(new.MusicalKey, '') || ' ' || (CASE new.MusicalKey
                            WHEN 'Abm' THEN '1A' WHEN 'G#m' THEN '1A' WHEN 'B' THEN '1B' WHEN 'Cb' THEN '1B'
                            WHEN 'Ebm' THEN '2A' WHEN 'D#m' THEN '2A' WHEN 'F#' THEN '2B' WHEN 'Gb' THEN '2B'
                            WHEN 'Bbm' THEN '3A' WHEN 'A#m' THEN '3A' WHEN 'Db' THEN '3B' WHEN 'C#' THEN '3B'
                            WHEN 'Fm' THEN '4A' WHEN 'Ab' THEN '4B' WHEN 'G#' THEN '4B'
                            WHEN 'Cm' THEN '5A' WHEN 'Eb' THEN '5B' WHEN 'D#' THEN '5B'
                            WHEN 'Gm' THEN '6A' WHEN 'Bb' THEN '6B' WHEN 'A#' THEN '6B'
                            WHEN 'Dm' THEN '7A' WHEN 'F' THEN '7B'
                            WHEN 'Am' THEN '8A' WHEN 'C' THEN '8B'
                            WHEN 'Em' THEN '9A' WHEN 'G' THEN '9B'
                            WHEN 'Bm' THEN '10A' WHEN 'D' THEN '10B'
                            WHEN 'F#m' THEN '11A' WHEN 'Gbm' THEN '11A' WHEN 'A' THEN '11B'
                            WHEN 'Dbm' THEN '12A' WHEN 'C#m' THEN '12A' WHEN 'E' THEN '12B'
                            ELSE ''
                        END),
                        new.UniqueHash
                    );
                END;
            ";
            await command.ExecuteNonQueryAsync();

            // Seed initial data
            command.CommandText = "SELECT COUNT(*) FROM LibraryEntriesFts";
            var libFtsCount = Convert.ToInt64(await command.ExecuteScalarAsync());
            if (libFtsCount == 0 || isLibFtsIncorrect)
            {
                _logger.LogInformation("Seeding FTS5 library index...");
                command.CommandText = @"
                    INSERT INTO LibraryEntriesFts(Artist, Title, Album, Key, UniqueHash)
                    SELECT Artist, Title, Album,
                           COALESCE(MusicalKey, '') || ' ' || (CASE MusicalKey
                            WHEN 'Abm' THEN '1A' WHEN 'G#m' THEN '1A' WHEN 'B' THEN '1B' WHEN 'Cb' THEN '1B'
                            WHEN 'Ebm' THEN '2A' WHEN 'D#m' THEN '2A' WHEN 'F#' THEN '2B' WHEN 'Gb' THEN '2B'
                            WHEN 'Bbm' THEN '3A' WHEN 'A#m' THEN '3A' WHEN 'Db' THEN '3B' WHEN 'C#' THEN '3B'
                            WHEN 'Fm' THEN '4A' WHEN 'Ab' THEN '4B' WHEN 'G#' THEN '4B'
                            WHEN 'Cm' THEN '5A' WHEN 'Eb' THEN '5B' WHEN 'D#' THEN '5B'
                            WHEN 'Gm' THEN '6A' WHEN 'Bb' THEN '6B' WHEN 'A#' THEN '6B'
                            WHEN 'Dm' THEN '7A' WHEN 'F' THEN '7B'
                            WHEN 'Am' THEN '8A' WHEN 'C' THEN '8B'
                            WHEN 'Em' THEN '9A' WHEN 'G' THEN '9B'
                            WHEN 'Bm' THEN '10A' WHEN 'D' THEN '10B'
                            WHEN 'F#m' THEN '11A' WHEN 'Gbm' THEN '11A' WHEN 'A' THEN '11B'
                            WHEN 'Dbm' THEN '12A' WHEN 'C#m' THEN '12A' WHEN 'E' THEN '12B'
                            ELSE ''
                        END),
                        UniqueHash FROM LibraryEntries;";
                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("✅ Library FTS5 search index seeded successfully.");
            }

            // Phase 3: Set-Prep Intelligence tables
            if (!TableExists("SetLists"))
            {
                _logger.LogInformation("Patching Schema: Creating SetLists table...");
                command.CommandText = @"
                    CREATE TABLE ""SetLists"" (
                        ""Id"" TEXT NOT NULL CONSTRAINT ""PK_SetLists"" PRIMARY KEY,
                        ""Name"" TEXT NOT NULL,
                        ""CreatedAt"" TEXT NOT NULL,
                        ""LastModifiedAt"" TEXT NOT NULL,
                        ""FlowHealth"" REAL NOT NULL,
                        ""ForensicLogsJson"" TEXT NULL
                    );";
                await command.ExecuteNonQueryAsync();
            }

            if (!TableExists("SetTracks"))
            {
                _logger.LogInformation("Patching Schema: Creating SetTracks table...");
                command.CommandText = @"
                    CREATE TABLE ""SetTracks"" (
                        ""Id"" TEXT NOT NULL CONSTRAINT ""PK_SetTracks"" PRIMARY KEY,
                        ""SetListId"" TEXT NOT NULL,
                        ""TrackUniqueHash"" TEXT NOT NULL,
                        ""Position"" INTEGER NOT NULL,
                        ""TransitionType"" TEXT NOT NULL,
                        ""ManualOffset"" REAL NOT NULL,
                        ""TransitionReasoning"" TEXT NULL,
                        ""DjNotes"" TEXT NULL,
                        CONSTRAINT ""FK_SetTracks_SetLists_SetListId"" FOREIGN KEY (""SetListId"") REFERENCES ""SetLists"" (""Id"") ON DELETE CASCADE
                    );
                    CREATE INDEX ""IX_SetTracks_SetListId"" ON ""SetTracks"" (""SetListId"");
                ";
                await command.ExecuteNonQueryAsync();
            }

            // 15. Soft Clear: IsClearedFromDownloadCenter
            if (TableExists("PlaylistTracks"))
            {
                if (!ColumnExists("PlaylistTracks", "IsClearedFromDownloadCenter"))
                {
                    _logger.LogInformation("Patching Schema: Adding IsClearedFromDownloadCenter to PlaylistTracks...");
                    command.CommandText = @"ALTER TABLE ""PlaylistTracks"" ADD COLUMN ""IsClearedFromDownloadCenter"" INTEGER NOT NULL DEFAULT 0;";
                    await command.ExecuteNonQueryAsync();
                }
            }

            // 16. Phase 5: Deep DNA — 512-D Texture Embedding
            if (TableExists("audio_features"))
            {
                try
                {
                    if (!ColumnExists("audio_features", "DeepTextureEmbedding"))
                    {
                        _logger.LogInformation("Patching Schema: Adding DeepTextureEmbedding to audio_features...");
                        command.CommandText = @"ALTER TABLE ""audio_features"" ADD COLUMN ""DeepTextureEmbedding"" BLOB NULL;";
                        await command.ExecuteNonQueryAsync();
                        _logger.LogInformation("✅ DeepTextureEmbedding column added to audio_features");
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to patch DeepTextureEmbedding"); }
            }

            _logger.LogInformation("Schema patching completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply schema patches. Application may be unstable.");
        }
    }

    private async Task MigrateStemPreferencesFromJsonAsync()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var jsonPath = System.IO.Path.Combine(appData, "ORBIT", "stem_preferences.json");

            if (!System.IO.File.Exists(jsonPath)) return;

            _logger.LogInformation("Migrating Stem Preferences from JSON to SQLite...");
            var jsonContent = await System.IO.File.ReadAllTextAsync(jsonPath);
            var preferences = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, StemPreference>>(jsonContent);

            if (preferences == null || !preferences.Any()) return;

            using (var scope = new SqliteConnection($"Data Source={System.IO.Path.Combine(appData, "ORBIT", "library.db")}"))
            {
                await scope.OpenAsync();
                foreach (var (trackId, pref) in preferences)
                {
                    using (var cmd = scope.CreateCommand())
                    {
                        cmd.CommandText = @"
                            INSERT OR IGNORE INTO ""StemPreferences"" (""Id"", ""TrackUniqueHash"", ""AlwaysMutedJson"", ""AlwaysSoloJson"", ""LastModified"")
                            VALUES (@Id, @Hash, @Muted, @Solo, @Modified)";
                        
                        cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString());
                        cmd.Parameters.AddWithValue("@Hash", trackId);
                        cmd.Parameters.AddWithValue("@Muted", System.Text.Json.JsonSerializer.Serialize(pref.AlwaysMuted));
                        cmd.Parameters.AddWithValue("@Solo", System.Text.Json.JsonSerializer.Serialize(pref.AlwaysSolo));
                        cmd.Parameters.AddWithValue("@Modified", DateTime.Now.ToString("O"));
                        
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }

            _logger.LogInformation("Successfully migrated {Count} stem preferences. Archiving JSON file.", preferences.Count);
            System.IO.File.Move(jsonPath, jsonPath + ".old", overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate stem preferences from JSON");
        }
    }

    // Helper class for migration
    private class StemPreference
    {
        public List<int> AlwaysMuted { get; set; } = new();
        public List<int> AlwaysSolo { get; set; } = new();
    }
}
