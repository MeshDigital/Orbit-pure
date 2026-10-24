using System;
using System.IO;
using Microsoft.Data.Sqlite;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Backlog E1/B6: VACUUM used to run on every launch (blocking the UI thread), and the backup
/// helper had no caller and used a WAL-unsafe file copy. Every test uses its own temp DB path.
/// </summary>
public class LibraryDatabaseMaintenanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orbit-maint-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "library.db");

    public LibraryDatabaseMaintenanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private SqliteConnection OpenWalDb()
    {
        var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE IF NOT EXISTS t(v TEXT);";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static long Count(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void IsDue_HandlesMissingRecentStaleAndFutureMarkers()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var week = TimeSpan.FromDays(7);
        Assert.True(LibraryDatabaseMaintenance.IsDue(null, week, now));
        Assert.False(LibraryDatabaseMaintenance.IsDue(now.AddDays(-1), week, now));
        Assert.True(LibraryDatabaseMaintenance.IsDue(now.AddDays(-7), week, now));
        Assert.True(LibraryDatabaseMaintenance.IsDue(now.AddDays(3), week, now)); // clock moved back
    }

    [Fact]
    public void BackupDirectory_IsDerivedFromTheDatabasePath()
    {
        Assert.Equal(Path.Combine(_dir, "Backups"), LibraryDatabaseMaintenance.GetBackupDirectory(DbPath));
    }

    [Fact]
    public void CreateBackup_IncludesRowsStillInTheWalFile()
    {
        using var conn = OpenWalDb();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO t VALUES ('a'),('b'),('c');";
            cmd.ExecuteNonQuery();
        }
        Assert.True(File.Exists(DbPath + "-wal"));

        var backup = LibraryDatabaseMaintenance.CreateBackup(DbPath);

        Assert.StartsWith(Path.Combine(_dir, "Backups"), backup);
        Assert.Equal(3, Count(backup));
    }

    [Fact]
    public void CreateBackup_MissingDatabase_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => LibraryDatabaseMaintenance.CreateBackup(Path.Combine(_dir, "nope.db")));
    }

    [Fact]
    public void RunScheduledVacuum_RunsOnceThenWaitsAWeek()
    {
        using (OpenWalDb()) { }
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        Exception? error = null;

        Assert.True(LibraryDatabaseMaintenance.RunScheduledVacuum(DbPath, now, ex => error = ex));
        Assert.Null(error);
        Assert.False(LibraryDatabaseMaintenance.RunScheduledVacuum(DbPath, now.AddDays(2)));
        Assert.True(LibraryDatabaseMaintenance.RunScheduledVacuum(DbPath, now.AddDays(8)));
    }

    [Fact]
    public void RunScheduledVacuum_MissingDatabase_DoesNothingAndDoesNotThrow()
    {
        Assert.False(LibraryDatabaseMaintenance.RunScheduledVacuum(Path.Combine(_dir, "nope.db")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "Backups")));
    }
}
