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

public partial class SchemaMigratorService
{
    private readonly ILogger<SchemaMigratorService> _logger;
    private static readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);

    public SchemaMigratorService(ILogger<SchemaMigratorService> logger)
    {
        _logger = logger;
    }


    public async Task InitializeDatabaseAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[{Ms}ms] Database Init: Starting", sw.ElapsedMilliseconds);
        var appData = SLSKDONET.Data.OrbitPaths.RoamingRoot;
        var dbPath = SLSKDONET.Data.OrbitPaths.LibraryDbPath;

        // Phase 24: Automatic Database Backup & Recovery
        await CheckForForceResetAsync().ConfigureAwait(false); // Step 1: Check if user requested reset
        await PerformBackupAsync().ConfigureAwait(false);      // Step 2: Backup existing or Restore if missing

        // Validate active DB before any migration/PRAGMA work.
        // If malformed, try healthy backup fallback; otherwise start fresh.
        if (System.IO.File.Exists(dbPath))
        {
            var isHealthy = await IsSqliteDatabaseHealthyAsync(dbPath).ConfigureAwait(false);
            if (!isHealthy)
            {
                _logger.LogWarning("Active database is malformed. Attempting recovery from healthy backup before migrations.");

                var backupDir = LibraryDatabaseMaintenance.GetBackupDirectory(dbPath);
                var restored = await TryRestoreLatestHealthyBackupAsync(backupDir, dbPath).ConfigureAwait(false);

                if (!restored)
                {
                    _logger.LogWarning("No healthy backup found. Recreating database from scratch.");
                    await DeleteDatabaseFilesAsync(dbPath).ConfigureAwait(false);
                }
                else
                {
                    var restoredHealthy = await IsSqliteDatabaseHealthyAsync(dbPath).ConfigureAwait(false);
                    if (!restoredHealthy)
                    {
                        _logger.LogWarning("Recovered backup still malformed. Recreating database from scratch.");
                        await DeleteDatabaseFilesAsync(dbPath).ConfigureAwait(false);
                    }
                }
            }
        }

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
            .ConfigureWarnings(warnings =>
            {
                warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning);
            })
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

                // Safety net behind the migrations and the explicit patches above: whatever the
                // EF model maps but the database still lacks (a brand-new install was missing 13
                // columns, 2026-10-19) is added here, additively. See SchemaReconciler.
                SchemaReconciler.Reconcile(context, connection, _logger);
                _logger.LogInformation("[{Ms}ms] Database Init: schema reconciliation finished", sw.ElapsedMilliseconds);
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


    private async Task MigrateStemPreferencesFromJsonAsync()
    {
        try
        {
            var appData = SLSKDONET.Data.OrbitPaths.RoamingRoot;
            var jsonPath = System.IO.Path.Combine(appData, "ORBIT", "stem_preferences.json");

            if (!System.IO.File.Exists(jsonPath)) return;

            _logger.LogInformation("Migrating Stem Preferences from JSON to SQLite...");
            var jsonContent = await System.IO.File.ReadAllTextAsync(jsonPath);
            var preferences = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, StemPreference>>(jsonContent);

            if (preferences == null || !preferences.Any()) return;

            using (var scope = new SqliteConnection($"Data Source={SLSKDONET.Data.OrbitPaths.LibraryDbPath}"))
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
