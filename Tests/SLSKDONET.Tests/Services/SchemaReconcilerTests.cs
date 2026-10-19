using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Tests.Architecture;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// <see cref="SchemaReconciler"/> on a database that is missing tables, columns and unique keys —
/// the shape an old raw-SQL-patched DB or a drifted fresh install can have. It must add what is
/// missing, keep every existing row, never throw, and be a no-op on the second run.
/// </summary>
public class SchemaReconcilerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orbit-tests", "reconcile-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public SchemaReconcilerTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "library.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException ex) { Console.WriteLine($"SchemaReconcilerTests: could not delete {_dir}: {ex.Message}"); }
    }

    private string ConnectionString => $"Data Source={_dbPath};Pooling=False";

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString).Options);

    private void Exec(string sql)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private SchemaReconciliationReport Reconcile()
    {
        using var context = CreateContext();
        var connection = context.Database.GetDbConnection();
        connection.Open();
        return SchemaReconciler.Reconcile(context, connection, NullLogger.Instance);
    }

    private void CreateLegacySchema()
    {
        // A cut-down legacy DB: two of the model's tables with a fraction of their columns and no
        // unique key on audio_features.TrackUniqueHash; every other table missing entirely.
        Exec(@"
            CREATE TABLE ""LibraryEntries"" (""UniqueHash"" TEXT NOT NULL PRIMARY KEY, ""Artist"" TEXT NOT NULL, ""Title"" TEXT NOT NULL);
            CREATE TABLE ""audio_features"" (""Id"" TEXT NOT NULL PRIMARY KEY, ""TrackUniqueHash"" TEXT NOT NULL, ""Bpm"" REAL NOT NULL DEFAULT 0);
            CREATE INDEX ""IX_audio_features_TrackUniqueHash"" ON ""audio_features"" (""TrackUniqueHash"");
            INSERT INTO ""LibraryEntries"" VALUES ('hash-1', 'Old Artist', 'Old Title');
            INSERT INTO ""LibraryEntries"" VALUES ('hash-2', 'Other Artist', 'Other Title');
            INSERT INTO ""audio_features"" (""Id"", ""TrackUniqueHash"", ""Bpm"") VALUES ('00000000-0000-0000-0000-000000000001', 'hash-1', 174.5);");
    }

    [Fact]
    public void Reconcile_AddsMissingTablesColumnsAndUniqueKeys_KeepsData_AndIsIdempotent()
    {
        CreateLegacySchema();

        var first = Reconcile();

        Assert.Empty(first.Failures);
        Assert.Contains("PlaylistTracks", first.CreatedTables);
        Assert.Contains("audio_features.VectorEmbedding", first.AddedColumns);
        Assert.Contains(first.CreatedIndexes, i => i.StartsWith("audio_features.", StringComparison.Ordinal));

        using (var conn = new SqliteConnection(ConnectionString))
        {
            conn.Open();
            Assert.Empty(FreshDatabaseSchemaParityTests.FindMissingSchema(conn));
        }

        // Existing rows survive with their values; new NOT NULL columns carry a safe default.
        Assert.Equal("Old Artist", Scalar("SELECT Artist FROM LibraryEntries WHERE UniqueHash='hash-1'"));
        Assert.Equal(174.5, Convert.ToDouble(Scalar("SELECT Bpm FROM audio_features WHERE TrackUniqueHash='hash-1'")));
        Assert.Equal(0.0, Convert.ToDouble(Scalar("SELECT Energy FROM audio_features WHERE TrackUniqueHash='hash-1'")));
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT EnrichmentAttempts FROM LibraryEntries WHERE UniqueHash='hash-1'")));

        // LibraryEntries.Id (an alternate key) didn't exist: existing rows got distinct Guids, not
        // one shared zero Guid, so the unique key could be created and EF can track them all.
        Assert.Equal(2L, Convert.ToInt64(Scalar("SELECT COUNT(DISTINCT Id) FROM LibraryEntries")));
        Assert.Contains(first.CreatedIndexes, i => i.StartsWith("LibraryEntries.", StringComparison.Ordinal));

        // EF can now read and write the patched tables.
        using (var context = CreateContext())
        {
            var all = context.LibraryEntries.ToList();
            Assert.Equal(2, all.Count);
            Assert.All(all, e => Assert.NotEqual(Guid.Empty, e.Id));
        }
        using (var context = CreateContext())
        {
            var entry = context.LibraryEntries.Include(e => e.AudioFeatures).Single(e => e.UniqueHash == "hash-1");
            Assert.Equal(174.5, entry.AudioFeatures!.Bpm, 3);
            entry.AudioFeatures.VectorEmbedding = new float[] { 1f, 2f };
            context.SaveChanges();
        }

        // The unique key now holds: a second features row for the same track is rejected.
        Assert.Throws<SqliteException>(() => Exec(
            "INSERT INTO audio_features (Id, TrackUniqueHash) VALUES ('00000000-0000-0000-0000-000000000002', 'hash-1');"));

        var second = Reconcile();
        Assert.False(second.HasChanges, "second run should find nothing to patch: " + second);
        Assert.Empty(second.Failures);
    }

    [Fact]
    public void Reconcile_DuplicateRowsBlockingAUniqueIndex_ReportsFailure_KeepsRows_PatchesTheRest()
    {
        CreateLegacySchema();
        Exec("INSERT INTO audio_features (Id, TrackUniqueHash, Bpm) VALUES ('00000000-0000-0000-0000-000000000009', 'hash-1', 120);");

        var report = Reconcile();

        Assert.Contains(report.Failures, f => f.Contains("unique index", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2L, Convert.ToInt64(Scalar("SELECT COUNT(*) FROM audio_features WHERE TrackUniqueHash='hash-1'")));
        Assert.Contains("audio_features.VectorEmbedding", report.AddedColumns);
        Assert.Contains("PlaylistTracks", report.CreatedTables);
    }

    [Fact]
    public void Reconcile_OnACompleteSchema_ChangesNothing()
    {
        using (var context = CreateContext())
            context.Database.EnsureCreated();

        var report = Reconcile();

        Assert.False(report.HasChanges, report.ToString());
        Assert.Empty(report.Failures);
    }
}
