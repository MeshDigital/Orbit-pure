using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Guard: a brand-new install must get a database that matches the EF model.
/// <para>
/// 2026-10-19: <c>ORBIT.exe --setup-preview</c> (fresh profile, fresh library.db) failed every
/// library query with "no such column: a.VectorEmbedding" — the EF migrations a new DB is built
/// from had drifted from the model, and existing users never noticed because their old DBs had
/// picked the columns up through raw-SQL patches. The suite's shared test DB is built with
/// <c>EnsureCreated()</c> from the model, so no test could see it either. These tests build the
/// DB through the real startup path (<see cref="DatabaseService.InitAsync"/> →
/// <see cref="SchemaMigratorService"/>) on an empty file and compare every mapped table and column.
/// </para>
/// </summary>
[Collection(NonParallelCollection.Name)]
public class FreshDatabaseSchemaParityTests
{
    internal static DatabaseService CreateDatabaseService() =>
        new(
            NullLogger<DatabaseService>.Instance,
            new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance),
            new TrackRepository(NullLogger<TrackRepository>.Instance),
            new Mock<IFileWriteService>().Object);

    /// <summary>Every (table, column) the EF model maps, as SQLite sees them (case-insensitive).</summary>
    internal static Dictionary<string, List<string>> ExpectedSchema()
    {
        using var context = new AppDbContext();
        var expected = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table == null) continue;
            var storeObject = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            if (!expected.TryGetValue(table, out var columns))
                expected[table] = columns = new List<string>();
            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName(storeObject);
                if (column != null && !columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    columns.Add(column);
            }
        }
        return expected;
    }

    internal static List<string> FindMissingSchema(SqliteConnection connection)
    {
        var missing = new List<string>();
        foreach (var (table, columns) in ExpectedSchema().OrderBy(kv => kv.Key))
        {
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM pragma_table_info($t)";
                cmd.Parameters.AddWithValue("$t", table);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) actual.Add(reader.GetString(0));
            }

            if (actual.Count == 0)
            {
                missing.Add($"TABLE {table}");
                continue;
            }
            missing.AddRange(columns.Where(c => !actual.Contains(c)).Select(c => $"COLUMN {table}.{c}"));
        }
        return missing;
    }

    [Fact]
    public async Task FreshDatabase_HasEveryTableAndColumnInTheEfModel()
    {
        using var scope = new FreshDatabaseScope();
        await CreateDatabaseService().InitAsync();

        using var connection = scope.OpenConnection();
        var missing = FindMissingSchema(connection);

        Assert.True(missing.Count == 0,
            $"A brand-new database is missing {missing.Count} mapped table(s)/column(s):\n  " +
            string.Join("\n  ", missing));
    }

    /// <summary>
    /// The reverse direction: a column the model no longer maps (or maps under another name) is
    /// harmless only if inserts can leave it out. NOT NULL without a DEFAULT breaks every EF insert.
    /// </summary>
    [Fact]
    public async Task FreshDatabase_HasNoUnmappedNotNullColumnWithoutDefault()
    {
        using var scope = new FreshDatabaseScope();
        await CreateDatabaseService().InitAsync();

        using var connection = scope.OpenConnection();
        var offenders = new List<string>();
        foreach (var (table, columns) in ExpectedSchema())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name, \"notnull\", dflt_value, pk FROM pragma_table_info($t)";
            cmd.Parameters.AddWithValue("$t", table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var notNull = reader.GetInt64(1) == 1;
                var hasDefault = !reader.IsDBNull(2);
                var isPk = reader.GetInt64(3) > 0;
                if (notNull && !hasDefault && !isPk && !columns.Contains(name, StringComparer.OrdinalIgnoreCase))
                    offenders.Add($"{table}.{name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Unmapped NOT NULL columns without a DEFAULT (EF inserts will fail): " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task FreshDatabase_HasEveryUniqueKeyTheModelDeclares()
    {
        using var scope = new FreshDatabaseScope();
        await CreateDatabaseService().InitAsync();

        using var connection = scope.OpenConnection();
        using var context = new AppDbContext();
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var missing = new List<string>();
        foreach (var table in model.Tables)
        {
            var wanted = table.Indexes.Where(i => i.IsUnique).Select(i => (i.Name, Cols: i.Columns.Select(c => c.Name).ToList()))
                .Concat(table.UniqueConstraints.Select(k => (k.Name, Cols: k.Columns.Select(c => c.Name).ToList())));
            var existing = UniqueIndexColumnSets(connection, table.Name);
            foreach (var (name, cols) in wanted)
            {
                if (!existing.Any(e => e.SetEquals(cols)))
                    missing.Add($"{table.Name}.{name} ({string.Join(",", cols)})");
            }
        }

        Assert.True(missing.Count == 0, "Missing unique indexes/keys: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The queries behind the screens that broke on the owner's fresh install, run against a
    /// fresh DB with one row in each table they touch (so inserts are exercised too).
    /// </summary>
    [Fact]
    public async Task FreshDatabase_RepresentativeLibraryQueriesDoNotThrow()
    {
        using var scope = new FreshDatabaseScope();
        var db = CreateDatabaseService();
        await db.InitAsync();

        const string hash = "fresh-db-hash-1";
        var jobId = Guid.NewGuid();
        using (var context = new AppDbContext())
        {
            context.LibraryEntries.Add(new SLSKDONET.Data.LibraryEntryEntity
            {
                UniqueHash = hash, Artist = "Artist", Title = "Title", FilePath = @"C:\music\a.flac",
                AddedAt = DateTime.UtcNow,
            });
            context.AudioFeatures.Add(new SLSKDONET.Data.Entities.AudioFeaturesEntity
            {
                TrackUniqueHash = hash, Bpm = 174, VectorEmbedding = new float[] { 0.1f, 0.2f },
            });
            context.Projects.Add(new SLSKDONET.Data.PlaylistJobEntity { Id = jobId, SourceTitle = "Fresh", CreatedAt = DateTime.UtcNow });
            context.PlaylistTracks.Add(new SLSKDONET.Data.PlaylistTrackEntity
            {
                Id = Guid.NewGuid(), PlaylistId = jobId, Artist = "Artist", Title = "Title", TrackUniqueHash = hash,
                Status = SLSKDONET.Models.TrackStatus.Downloaded, ResolvedFilePath = @"C:\music\a.flac", AddedAt = DateTime.UtcNow,
            });
            context.Tracks.Add(new SLSKDONET.Data.TrackEntity { GlobalId = hash, Artist = "Artist", Title = "Title" });
            context.AnalysisRuns.Add(new SLSKDONET.Data.Entities.AnalysisRunEntity { TrackUniqueHash = hash });
            context.CuePoints.Add(new SLSKDONET.Data.Entities.CuePointEntity { TrackUniqueHash = hash, TimestampInSeconds = 1 });
            var set = new SLSKDONET.Data.Entities.SetListEntity { Name = "Set" };
            set.Tracks.Add(new SLSKDONET.Data.Entities.SetTrackEntity { TrackUniqueHash = hash, Position = 0 });
            context.SetLists.Add(set);
            await context.SaveChangesAsync();
        }

        // Analysis page library load (LifecycleProjectionService → LoadAllLibraryEntriesAsync).
        var entries = await db.GetAllLibraryEntriesAsync();
        Assert.Contains(entries, e => e.UniqueHash == hash);
        Assert.NotNull(await db.FindLibraryEntryAsync(hash));
        Assert.NotEmpty(await db.LoadAllAudioFeaturesAsync());
        Assert.NotNull(await db.GetAudioFeaturesByHashAsync(hash));

        // Library → All Tracks (count + paging, every sort) and a real playlist.
        Assert.True(await db.GetTotalLibraryTrackCountAsync() >= 1);
        foreach (var sort in Enum.GetValues<SLSKDONET.Models.TrackSortColumn>())
            await db.GetPagedAllTracksAsync(0, 50, sortColumn: sort);
        Assert.Equal(1, await db.GetPlaylistTrackCountAsync(jobId));
        Assert.Single(await db.GetPagedPlaylistTracksAsync(jobId, 0, 50));
        Assert.Single(await db.LoadPlaylistTracksAsync(jobId));
        Assert.NotEmpty(await db.LoadAllPlaylistJobsAsync());

        // Download / analysis queues, inspector side data.
        await db.LoadQueueAsync();
        await db.LoadPersistedDownloadQueueTracksAsync();
        await db.GetAllPlaylistTracksAsync();
        await db.GetPhrasesByHashAsync(hash);
        await db.LoadAllStyleDefinitionsAsync();
        await db.GetRecentDownloadHistoryAsync();
        using (var context = new AppDbContext())
        {
            Assert.NotNull(await context.PlaylistTracks.Include(t => t.AudioFeatures).FirstOrDefaultAsync(t => t.TrackUniqueHash == hash));
            Assert.NotNull(await context.LibraryEntries.Include(e => e.AudioFeatures).FirstOrDefaultAsync(e => e.UniqueHash == hash));
            Assert.NotEmpty(await context.AnalysisRuns.Where(r => r.TrackUniqueHash == hash).ToListAsync());
            Assert.NotEmpty(await context.CuePoints.Where(c => c.TrackUniqueHash == hash).ToListAsync());
            Assert.NotEmpty(await context.SetLists.Include(s => s.Tracks).ToListAsync());
            Assert.NotEmpty(await context.Tracks.ToListAsync());
        }
    }

    private static List<HashSet<string>> UniqueIndexColumnSets(SqliteConnection connection, string table)
    {
        var names = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_index_list($t) WHERE \"unique\" = 1";
            cmd.Parameters.AddWithValue("$t", table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }

        var sets = new List<HashSet<string>>();
        foreach (var name in names)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_index_info($i)";
            cmd.Parameters.AddWithValue("$i", name);
            using var reader = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) if (!reader.IsDBNull(0)) set.Add(reader.GetString(0));
            sets.Add(set);
        }

        // An INTEGER PRIMARY KEY is the rowid and has no index entry; a declared PK still counts.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info($t) WHERE pk > 0";
            cmd.Parameters.AddWithValue("$t", table);
            using var reader = cmd.ExecuteReader();
            var pk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) pk.Add(reader.GetString(0));
            if (pk.Count > 0) sets.Add(pk);
        }
        return sets;
    }
}
