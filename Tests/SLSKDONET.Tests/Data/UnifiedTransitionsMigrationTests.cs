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
public sealed class UnifiedTransitionsMigrationTests
{
    [Fact]
    public async Task Backfill_CopiesPlaylistAndSetRows_SkipsOrphans_HandlesNullHashes_KeepsDuplicatePairs_AndIsIdempotent()
    {
        using var scope = new FreshDatabaseScope();

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

            // Recreate the legacy tables to simulate upgrading from v0.4.0 / v0.5.1 (FO1a + FO1f #523).
            await initCtx.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""PlaylistTrackTransitions"" (
                    ""Id"" TEXT NOT NULL PRIMARY KEY,
                    ""PlaylistId"" TEXT NOT NULL,
                    ""OutgoingPlaylistTrackId"" TEXT NOT NULL,
                    ""IncomingPlaylistTrackId"" TEXT NOT NULL,
                    ""PresetName"" TEXT NOT NULL DEFAULT 'Auto',
                    ""TransitionType"" TEXT NOT NULL DEFAULT 'Crossfade',
                    ""DurationBars"" INTEGER NOT NULL DEFAULT 16,
                    ""SourceTriggerSeconds"" REAL NULL,
                    ""TargetTriggerSeconds"" REAL NULL,
                    ""EchoDecayFactor"" REAL NULL,
                    ""FilterStartFrequency"" REAL NULL,
                    ""FilterEndFrequency"" REAL NULL,
                    ""EqLowGain"" REAL NULL,
                    ""EqMidGain"" REAL NULL,
                    ""EqHighGain"" REAL NULL,
                    ""WaveDuckDepth"" REAL NULL,
                    ""FilterSweepRising"" INTEGER NULL,
                    ""EqSwapLow"" INTEGER NULL,
                    ""EqSwapMid"" INTEGER NULL,
                    ""EqSwapHigh"" INTEGER NULL,
                    ""EqLowCrossoverHz"" REAL NULL,
                    ""EqHighCrossoverHz"" REAL NULL,
                    ""LoopBars"" INTEGER NULL,
                    ""LoopRepeats"" INTEGER NULL,
                    ""UpdatedAtUtc"" TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ""SetTrackTransitions"" (
                    ""Id"" TEXT NOT NULL PRIMARY KEY,
                    ""SetId"" TEXT NOT NULL,
                    ""OutgoingSlotId"" TEXT NOT NULL,
                    ""IncomingSlotId"" TEXT NOT NULL,
                    ""PresetName"" TEXT NOT NULL DEFAULT 'Auto',
                    ""TransitionType"" TEXT NOT NULL DEFAULT 'Crossfade',
                    ""DurationBars"" INTEGER NOT NULL DEFAULT 16,
                    ""SourceTriggerSeconds"" REAL NULL,
                    ""TargetTriggerSeconds"" REAL NULL,
                    ""EchoDecayFactor"" REAL NULL,
                    ""FilterStartFrequency"" REAL NULL,
                    ""FilterEndFrequency"" REAL NULL,
                    ""WaveDuckDepth"" REAL NULL,
                    ""FilterSweepRising"" INTEGER NULL,
                    ""EqSwapLow"" INTEGER NULL,
                    ""EqSwapMid"" INTEGER NULL,
                    ""EqSwapHigh"" INTEGER NULL,
                    ""EqLowCrossoverHz"" REAL NULL,
                    ""EqHighCrossoverHz"" REAL NULL,
                    ""LoopBars"" INTEGER NULL,
                    ""LoopRepeats"" INTEGER NULL,
                    ""UpdatedAtUtc"" TEXT NOT NULL
                );");

            await initCtx.SaveChangesAsync();

            // Playlist transitions: valid pair #1, valid reprise #2, null-hash pair, and orphaned pair
            await initCtx.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""PlaylistTrackTransitions"" (""Id"", ""PlaylistId"", ""OutgoingPlaylistTrackId"", ""IncomingPlaylistTrackId"", ""PresetName"", ""TransitionType"", ""DurationBars"", ""EqSwapLow"", ""UpdatedAtUtc"")
                  VALUES ({0}, {1}, {2}, {3}, 'Blend', 'Crossfade', 16, 1, '2026-11-20T12:00:00Z');",
                Guid.NewGuid(), playlistId, pt1.Id, pt2.Id);
            await initCtx.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""PlaylistTrackTransitions"" (""Id"", ""PlaylistId"", ""OutgoingPlaylistTrackId"", ""IncomingPlaylistTrackId"", ""PresetName"", ""TransitionType"", ""DurationBars"", ""UpdatedAtUtc"")
                  VALUES ({0}, {1}, {2}, {3}, 'Melt', 'EchoOut', 8, '2026-11-21T12:00:00Z');",
                Guid.NewGuid(), playlistId, pt3.Id, pt4.Id);
            await initCtx.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""PlaylistTrackTransitions"" (""Id"", ""PlaylistId"", ""OutgoingPlaylistTrackId"", ""IncomingPlaylistTrackId"", ""PresetName"", ""TransitionType"", ""DurationBars"", ""UpdatedAtUtc"")
                  VALUES ({0}, {1}, {2}, {3}, 'Fade', 'Crossfade', 8, '2026-11-22T12:00:00Z');",
                Guid.NewGuid(), playlistId, pt2.Id, ptNoHash.Id);
            await initCtx.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""PlaylistTrackTransitions"" (""Id"", ""PlaylistId"", ""OutgoingPlaylistTrackId"", ""IncomingPlaylistTrackId"", ""PresetName"", ""TransitionType"", ""DurationBars"", ""UpdatedAtUtc"")
                  VALUES ({0}, {1}, {2}, {3}, 'Rise', 'FilterSweep', 8, '2026-11-23T12:00:00Z');",
                Guid.NewGuid(), playlistId, Guid.NewGuid(), pt2.Id);

            // Set transition: valid pair
            await initCtx.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""SetTrackTransitions"" (""Id"", ""SetId"", ""OutgoingSlotId"", ""IncomingSlotId"", ""PresetName"", ""TransitionType"", ""DurationBars"", ""FilterSweepRising"", ""UpdatedAtUtc"")
                  VALUES ({0}, {1}, {2}, {3}, 'Rise', 'FilterSweep', 16, 1, '2026-11-24T12:00:00Z');",
                Guid.NewGuid(), setId, st1.Id, st2.Id);
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

        // 4. FO1f (#523): both legacy tables must be dropped after backfill verification.
        var conn = verifyCtx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('PlaylistTrackTransitions', 'SetTrackTransitions');";
        var legacyTableCount = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(0L, legacyTableCount);
    }
}
