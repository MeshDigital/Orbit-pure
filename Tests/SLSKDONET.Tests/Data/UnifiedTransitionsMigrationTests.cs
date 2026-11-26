using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Data;

[Collection(NonParallelCollection.Name)]
public sealed class UnifiedTransitionsMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string? _prevDbPath;

    public UnifiedTransitionsMigrationTests()
    {
        var isolatedRoot = Path.GetDirectoryName(OrbitPaths.LibraryDbPath)!;
        Directory.CreateDirectory(isolatedRoot);
        _dbPath = Path.Combine(isolatedRoot, $"fo1a-{Guid.NewGuid():N}.db");
        _prevDbPath = Environment.GetEnvironmentVariable("ORBIT_DB_PATH");
        Environment.SetEnvironmentVariable("ORBIT_DB_PATH", _dbPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ORBIT_DB_PATH", _prevDbPath);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [Fact]
    public async Task Backfill_CopiesPlaylistAndSetRows_SkipsOrphans_HandlesNullHashes_KeepsDuplicatePairs_AndIsIdempotent()
    {
        // 1. Initialize a real migrated DB, then drop Transitions to simulate an existing pre-FO1a install.
        var migrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        await migrator.InitializeDatabaseAsync();

        await using (var initCtx = new AppDbContext())
        {
            await initCtx.Database.ExecuteSqlRawAsync(@"DROP TABLE IF EXISTS ""Transitions"";");

            var playlistId = Guid.NewGuid();
            var setId = Guid.NewGuid();

            initCtx.Projects.Add(new PlaylistJobEntity { Id = playlistId, SourceTitle = "Club Mix", SourceType = "Local" });
            initCtx.SetLists.Add(new SetListEntity { Id = setId, Name = "Friday Night" });

            var pt1 = new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, Artist = "A", Title = "One", TrackUniqueHash = "hash-a", ResolvedFilePath = "" };
            var pt2 = new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, Artist = "B", Title = "Two", TrackUniqueHash = "hash-b", ResolvedFilePath = "" };
            // Same track pair occurring twice in one playlist (reprise)
            var pt3 = new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, Artist = "A", Title = "One", TrackUniqueHash = "hash-a", ResolvedFilePath = "" };
            var pt4 = new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, Artist = "B", Title = "Two", TrackUniqueHash = "hash-b", ResolvedFilePath = "" };
            // Unanalysed track with empty hash
            var ptNoHash = new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, Artist = "C", Title = "Unanalysed", TrackUniqueHash = "", ResolvedFilePath = "" };

            initCtx.PlaylistTracks.AddRange(pt1, pt2, pt3, pt4, ptNoHash);

            var st1 = new SetTrackEntity { Id = Guid.NewGuid(), SetListId = setId, TrackUniqueHash = "hash-x", Position = 0 };
            var st2 = new SetTrackEntity { Id = Guid.NewGuid(), SetListId = setId, TrackUniqueHash = "hash-y", Position = 1 };
            initCtx.SetTracks.AddRange(st1, st2);

            // Playlist transitions: valid pair #1, valid reprise #2, null-hash pair, and orphaned pair
            initCtx.PlaylistTrackTransitions.AddRange(
                new PlaylistTrackTransitionEntity
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = playlistId,
                    OutgoingPlaylistTrackId = pt1.Id,
                    IncomingPlaylistTrackId = pt2.Id,
                    PresetName = "Blend",
                    TransitionType = "Crossfade",
                    DurationBars = 16,
                    EqSwapLow = true,
                    UpdatedAtUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc)
                },
                new PlaylistTrackTransitionEntity
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = playlistId,
                    OutgoingPlaylistTrackId = pt3.Id,
                    IncomingPlaylistTrackId = pt4.Id,
                    PresetName = "Melt",
                    TransitionType = "EchoOut",
                    DurationBars = 8,
                    UpdatedAtUtc = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc)
                },
                new PlaylistTrackTransitionEntity
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = playlistId,
                    OutgoingPlaylistTrackId = pt2.Id,
                    IncomingPlaylistTrackId = ptNoHash.Id,
                    PresetName = "Fade",
                    TransitionType = "Crossfade",
                    DurationBars = 8,
                    UpdatedAtUtc = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc)
                },
                new PlaylistTrackTransitionEntity
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = playlistId,
                    OutgoingPlaylistTrackId = Guid.NewGuid(), // orphaned!
                    IncomingPlaylistTrackId = pt2.Id,
                    PresetName = "Rise",
                    TransitionType = "FilterSweep",
                    DurationBars = 8,
                    UpdatedAtUtc = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc)
                });

            // Set transition: valid pair
            initCtx.SetTrackTransitions.Add(new SetTrackTransitionEntity
            {
                Id = Guid.NewGuid(),
                SetId = setId,
                OutgoingSlotId = st1.Id,
                IncomingSlotId = st2.Id,
                PresetName = "Rise",
                TransitionType = "FilterSweep",
                DurationBars = 16,
                FilterSweepRising = true,
                UpdatedAtUtc = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc)
            });

            await initCtx.SaveChangesAsync();
        }

        // 2. Run SchemaMigratorService.InitializeDatabaseAsync twice to prove idempotency.
        await migrator.InitializeDatabaseAsync();
        await migrator.InitializeDatabaseAsync();

        // 3. Assert all rows migrated accurately.
        await using var verifyCtx = new AppDbContext();
        var rows = await verifyCtx.Transitions.AsNoTracking().ToListAsync();

        // 3 valid playlist rows (orphan skipped) + 1 valid set row = 4 total
        Assert.Equal(4, rows.Count);

        var playlistPairRows = rows.Where(r => r.ScopeKind == TransitionScopeKind.Playlist && r.OutgoingTrackHash == "hash-a" && r.IncomingTrackHash == "hash-b").ToList();
        Assert.Equal(2, playlistPairRows.Count);
        Assert.Contains(playlistPairRows, r => r.Technique == "EqSwap" && r.UseAsPairDefault && r.EqSwapLow == true);
        Assert.Contains(playlistPairRows, r => r.Technique == "EchoOut" && r.UseAsPairDefault && r.DurationBars == 8);

        var nullHashRow = Assert.Single(rows, r => r.ScopeKind == TransitionScopeKind.Playlist && r.IncomingTrackHash == null);
        Assert.False(nullHashRow.UseAsPairDefault);
        Assert.Equal("Crossfade", nullHashRow.Technique);

        var setRow = Assert.Single(rows, r => r.ScopeKind == TransitionScopeKind.Set);
        Assert.Equal("hash-x", setRow.OutgoingTrackHash);
        Assert.Equal("hash-y", setRow.IncomingTrackHash);
        Assert.True(setRow.UseAsPairDefault);
        Assert.Equal("FilterSweep", setRow.Technique);
        Assert.True(setRow.FilterSweepRising);
    }
}
