using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Services.Transitions;
using Xunit;

namespace SLSKDONET.Tests.Services.Transitions;

/// <summary>
/// Covers Epic #517 Phase 3 — #534 (FO3f: passive TransitionPlays log).
/// </summary>
[Collection(NonParallelCollection.Name)]
public class TransitionPlayLogTests
{
    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new AppDbContext();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext());
    }

    [Fact]
    public async Task MixedPair_LogsOneRowInTransitionPlays_AndNothingInTransitions()
    {
        using var scope = new FreshDatabaseScope();
        var migrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        await migrator.InitializeDatabaseAsync();
        // Idempotent second run
        await migrator.InitializeDatabaseAsync();

        var factory = new TestDbContextFactory();
        var t0 = new DateTime(2026, 11, 26, 14, 0, 0, DateTimeKind.Utc);
        var logService = new TransitionPlayLogService(factory, NullLogger<TransitionPlayLogService>.Instance, () => t0);

        var playId = await logService.RecordMixPlayAsync("hash-out", "hash-in", "EqSwap");

        Assert.NotNull(playId);

        await using var db = await factory.CreateDbContextAsync();
        var play = Assert.Single(await db.TransitionPlays.AsNoTracking().ToListAsync());
        Assert.Equal(playId.Value, play.Id);
        Assert.Equal("hash-out", play.OutgoingTrackHash);
        Assert.Equal("hash-in", play.IncomingTrackHash);
        Assert.Equal("EqSwap", play.Technique);
        Assert.False(play.WasSkipped);
        Assert.Equal(t0, play.PlayedAtUtc);

        // Critical invariant: passive play logging NEVER writes to Transitions
        Assert.Empty(await db.Transitions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SkipWithinWindow_MarksPlaySkipped_SkipAfterWindow_LeavesPlayedThrough()
    {
        using var scope = new FreshDatabaseScope();
        var factory = new TestDbContextFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var t0 = new DateTime(2026, 11, 26, 14, 0, 0, DateTimeKind.Utc);
        var logService = new TransitionPlayLogService(factory, NullLogger<TransitionPlayLogService>.Instance, () => t0);

        var skippedPlayId = await logService.RecordMixPlayAsync("h1", "h2", "EchoOut");
        var keptPlayId = await logService.RecordMixPlayAsync("h2", "h3", "Crossfade");

        // Skip 5 seconds after transition -> within 15s window -> WasSkipped = true
        await logService.MarkSkippedIfWithinWindowAsync(skippedPlayId!.Value, t0, t0.AddSeconds(5));

        // Skip 45 seconds after transition -> outside 15s window -> WasSkipped stays false
        await logService.MarkSkippedIfWithinWindowAsync(keptPlayId!.Value, t0, t0.AddSeconds(45));

        await using (var db = await factory.CreateDbContextAsync())
        {
            var rows = await db.TransitionPlays.AsNoTracking().ToListAsync();
            var skippedRow = Assert.Single(rows, r => r.Id == skippedPlayId.Value);
            var keptRow = Assert.Single(rows, r => r.Id == keptPlayId.Value);

            Assert.True(skippedRow.WasSkipped);
            Assert.False(keptRow.WasSkipped);
            Assert.Empty(await db.Transitions.AsNoTracking().ToListAsync());
        }
    }
}
