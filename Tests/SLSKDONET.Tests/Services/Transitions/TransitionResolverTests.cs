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

    [Fact]
    public async Task Upsert_UseAsPairDefaultFalse_RoundTrips_AndSecondSharedSaveWinsInThirdPlaylist()
    {
        using var scope = new FreshDatabaseScope();
        var migrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        await migrator.InitializeDatabaseAsync();

        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var p3 = Guid.NewGuid();
        var p1Out = Guid.NewGuid(); var p1In = Guid.NewGuid();
        var p2Out = Guid.NewGuid(); var p2In = Guid.NewGuid();

        await using (var db = new AppDbContext())
        {
            db.Projects.AddRange(
                new PlaylistJobEntity { Id = p1, SourceTitle = "First", SourceType = "Local" },
                new PlaylistJobEntity { Id = p2, SourceTitle = "Second", SourceType = "Local" },
                new PlaylistJobEntity { Id = p3, SourceTitle = "Third", SourceType = "Local" });
            db.PlaylistTracks.AddRange(
                new PlaylistTrackEntity { Id = p1Out, PlaylistId = p1, Artist = "A", Title = "1", TrackUniqueHash = "ha", ResolvedFilePath = "" },
                new PlaylistTrackEntity { Id = p1In, PlaylistId = p1, Artist = "B", Title = "2", TrackUniqueHash = "hb", ResolvedFilePath = "" },
                new PlaylistTrackEntity { Id = p2Out, PlaylistId = p2, Artist = "A", Title = "1", TrackUniqueHash = "ha", ResolvedFilePath = "" },
                new PlaylistTrackEntity { Id = p2In, PlaylistId = p2, Artist = "B", Title = "2", TrackUniqueHash = "hb", ResolvedFilePath = "" });
            await db.SaveChangesAsync();
        }

        var repo = new TransitionRepository(NullLogger<TransitionRepository>.Instance);
        var factory = new AppDbFactory();
        var resolver = new TransitionResolver(factory, new TransitionHistoryService(factory), NullLogger<TransitionResolver>.Instance);

        // Save in playlist 1 (shared by default)
        await repo.UpsertTransitionAsync(new PlaylistTrackTransition
        {
            PlaylistId = p1, OutgoingPlaylistTrackId = p1Out, IncomingPlaylistTrackId = p1In,
            PresetName = "Blend", DurationBars = 16, UseAsPairDefault = true
        });
        await Task.Delay(20);

        // Save in playlist 2 (shared) — second save must win in playlist 3
        await repo.UpsertTransitionAsync(new PlaylistTrackTransition
        {
            PlaylistId = p2, OutgoingPlaylistTrackId = p2Out, IncomingPlaylistTrackId = p2In,
            PresetName = "Rise", Type = TransitionType.FilterSweep, DurationBars = 8, UseAsPairDefault = true
        });

        var inP3 = await resolver.ResolveAsync(TransitionScopeKind.Playlist, p3, Guid.NewGuid(), Guid.NewGuid(), "ha", "hb");
        Assert.Equal(ResolvedTransitionSource.PairDefault, inP3.Source);
        Assert.Equal("FilterSweep", inP3.Technique);
        Assert.Equal("Second", inP3.EarlierMixSourceName);

        // Flip playlist 2 to "This playlist only" (UseAsPairDefault = false) — round-trips & playlist 1 now answers for playlist 3
        await repo.UpsertTransitionAsync(new PlaylistTrackTransition
        {
            PlaylistId = p2, OutgoingPlaylistTrackId = p2Out, IncomingPlaylistTrackId = p2In,
            PresetName = "Rise", Type = TransitionType.FilterSweep, DurationBars = 8, UseAsPairDefault = false
        });
        var p2Loaded = await repo.GetTransitionAsync(p2Out, p2In);
        Assert.False(p2Loaded!.UseAsPairDefault);

        var inP3After = await resolver.ResolveAsync(TransitionScopeKind.Playlist, p3, Guid.NewGuid(), Guid.NewGuid(), "ha", "hb");
        Assert.Equal(ResolvedTransitionSource.PairDefault, inP3After.Source);
        Assert.Equal("EqSwap", inP3After.Technique);
        Assert.Equal("First", inP3After.EarlierMixSourceName);
    }
}
