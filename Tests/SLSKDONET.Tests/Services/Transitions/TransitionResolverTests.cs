using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services;
using SLSKDONET.Services.Repositories;
using SLSKDONET.Services.Sets;
using SLSKDONET.Services.Transitions;
using Xunit;

namespace SLSKDONET.Tests.Services.Transitions;

[Collection(NonParallelCollection.Name)]
public sealed class TransitionResolverTests
{
    private sealed class AppDbFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
    }

    [Fact]
    public async Task ResolveAsync_FollowsThreeStepRule_ContainerThenNewestSharedPairThenAuto()
    {
        using var scope = new FreshDatabaseScope();
        var migrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        await migrator.InitializeDatabaseAsync();

        var factory = new AppDbFactory();
        var history = new TransitionHistoryService(factory);
        var resolver = new TransitionResolver(factory, history, NullLogger<TransitionResolver>.Instance);

        var playlist1 = Guid.NewGuid();
        var playlist2 = Guid.NewGuid();
        var playlist3 = Guid.NewGuid();

        var p1Out = Guid.NewGuid(); var p1In = Guid.NewGuid();
        var p2Out = Guid.NewGuid(); var p2In = Guid.NewGuid();
        var p3Out = Guid.NewGuid(); var p3In = Guid.NewGuid();

        await using (var db = new AppDbContext())
        {
            db.Projects.AddRange(
                new PlaylistJobEntity { Id = playlist1, SourceTitle = "Warmup Mix", SourceType = "Local" },
                new PlaylistJobEntity { Id = playlist2, SourceTitle = "Peak Mix", SourceType = "Local" },
                new PlaylistJobEntity { Id = playlist3, SourceTitle = "New Mix", SourceType = "Local" });

            db.Transitions.AddRange(
                new TransitionEntity
                {
                    ScopeKind = TransitionScopeKind.Playlist,
                    ScopeId = playlist1,
                    OutgoingItemId = p1Out,
                    IncomingItemId = p1In,
                    OutgoingTrackHash = "hash-a",
                    IncomingTrackHash = "hash-b",
                    UseAsPairDefault = true,
                    Technique = "EqSwap",
                    DurationBars = 16,
                    UpdatedAtUtc = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
                },
                new TransitionEntity
                {
                    ScopeKind = TransitionScopeKind.Playlist,
                    ScopeId = playlist2,
                    OutgoingItemId = p2Out,
                    IncomingItemId = p2In,
                    OutgoingTrackHash = "hash-a",
                    IncomingTrackHash = "hash-b",
                    UseAsPairDefault = true,
                    Technique = "FilterSweep",
                    DurationBars = 8,
                    UpdatedAtUtc = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
                });
            await db.SaveChangesAsync();
        }

        // Step 1: Container row wins inside playlist1 (even though playlist2 is newer)
        var step1 = await resolver.ResolveAsync(TransitionScopeKind.Playlist, playlist1, p1Out, p1In, "hash-a", "hash-b");
        Assert.Equal(ResolvedTransitionSource.Container, step1.Source);
        Assert.Equal("EqSwap", step1.Technique);
        Assert.Equal(16, step1.SavedTransition!.DurationBars);

        // Step 2: In playlist3 (no container row), newest shared row (playlist2) wins!
        var step2 = await resolver.ResolveAsync(TransitionScopeKind.Playlist, playlist3, p3Out, p3In, "hash-a", "hash-b");
        Assert.Equal(ResolvedTransitionSource.PairDefault, step2.Source);
        Assert.Equal("FilterSweep", step2.Technique);
        Assert.Equal("Peak Mix", step2.EarlierMixSourceName);
        Assert.False(step2.EarlierMixIsSet);

        // Step 3: Unseen pair falls back to Auto
        var step3 = await resolver.ResolveAsync(TransitionScopeKind.Playlist, playlist3, Guid.NewGuid(), Guid.NewGuid(), "hash-unseen-1", "hash-unseen-2");
        Assert.Equal(ResolvedTransitionSource.Auto, step3.Source);
        Assert.Null(step3.SavedTransition);
        Assert.Equal("Auto", step3.Technique);
    }

    [Fact]
    public async Task ResolveAsync_ThisPlaylistOnly_DoesNotLeakIntoAnotherPlaylist()
    {
        using var scope = new FreshDatabaseScope();
        var migrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        await migrator.InitializeDatabaseAsync();

        var factory = new AppDbFactory();
        var history = new TransitionHistoryService(factory);
        var resolver = new TransitionResolver(factory, history, NullLogger<TransitionResolver>.Instance);

        var playlist1 = Guid.NewGuid();
        var playlist2 = Guid.NewGuid();
        var p1Out = Guid.NewGuid(); var p1In = Guid.NewGuid();

        await using (var db = new AppDbContext())
        {
            db.Projects.Add(new PlaylistJobEntity { Id = playlist1, SourceTitle = "Private Mix", SourceType = "Local" });
            db.Transitions.Add(new TransitionEntity
            {
                ScopeKind = TransitionScopeKind.Playlist,
                ScopeId = playlist1,
                OutgoingItemId = p1Out,
                IncomingItemId = p1In,
                OutgoingTrackHash = "hash-only-1",
                IncomingTrackHash = "hash-only-2",
                UseAsPairDefault = false, // "This playlist only"
                Technique = "Cut",
                DurationBars = 4,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Still resolves inside playlist1 (Step 1)
        var inOwner = await resolver.ResolveAsync(TransitionScopeKind.Playlist, playlist1, p1Out, p1In, "hash-only-1", "hash-only-2");
        Assert.Equal(ResolvedTransitionSource.Container, inOwner.Source);
        Assert.Equal("Cut", inOwner.Technique);

        // Does NOT leak into playlist2 (falls back to Auto)
        var inOther = await resolver.ResolveAsync(TransitionScopeKind.Playlist, playlist2, Guid.NewGuid(), Guid.NewGuid(), "hash-only-1", "hash-only-2");
        Assert.Equal(ResolvedTransitionSource.Auto, inOther.Source);
        Assert.Null(inOther.SavedTransition);
    }
}
