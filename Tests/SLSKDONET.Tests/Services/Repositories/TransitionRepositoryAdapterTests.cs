using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services.Repositories;

[Collection(NonParallelCollection.Name)]
public sealed class TransitionRepositoryAdapterTests
{
    [Fact]
    public async Task Upsert_HandlesOrphanedItemIds_NullHashes_AndDuplicatePairsInSamePlaylist()
    {
        using var scope = new FreshDatabaseScope();
        var migrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        await migrator.InitializeDatabaseAsync();

        var playlistId = Guid.NewGuid();
        var pt1 = new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, Artist = "A", Title = "One", TrackUniqueHash = "hash-a", ResolvedFilePath = "" };
        var pt2 = new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, Artist = "B", Title = "Two", TrackUniqueHash = "hash-b", ResolvedFilePath = "" };
        var pt3 = new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, Artist = "A", Title = "One", TrackUniqueHash = "hash-a", ResolvedFilePath = "" };
        var pt4 = new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, Artist = "B", Title = "Two", TrackUniqueHash = "hash-b", ResolvedFilePath = "" };
        var ptNoHash = new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, Artist = "C", Title = "Unanalysed", TrackUniqueHash = "", ResolvedFilePath = "" };

        await using (var seed = new AppDbContext())
        {
            seed.Projects.Add(new PlaylistJobEntity { Id = playlistId, SourceTitle = "Club", SourceType = "Local" });
            seed.PlaylistTracks.AddRange(pt1, pt2, pt3, pt4, ptNoHash);
            await seed.SaveChangesAsync();
        }

        var repo = new TransitionRepository(NullLogger<TransitionRepository>.Instance);

        // 1. Same track pair occurring twice in one playlist (intro and reprise) keeps two distinct transitions.
        await repo.UpsertTransitionAsync(new PlaylistTrackTransition
        {
            PlaylistId = playlistId,
            OutgoingPlaylistTrackId = pt1.Id,
            IncomingPlaylistTrackId = pt2.Id,
            PresetName = "Blend",
            DurationBars = 16,
        });
        await repo.UpsertTransitionAsync(new PlaylistTrackTransition
        {
            PlaylistId = playlistId,
            OutgoingPlaylistTrackId = pt3.Id,
            IncomingPlaylistTrackId = pt4.Id,
            PresetName = "Melt",
            Type = TransitionType.EchoOut,
            DurationBars = 8,
        });

        // 2. Null/empty track hash persists container-scoped with UseAsPairDefault = false.
        await repo.UpsertTransitionAsync(new PlaylistTrackTransition
        {
            PlaylistId = playlistId,
            OutgoingPlaylistTrackId = pt2.Id,
            IncomingPlaylistTrackId = ptNoHash.Id,
            PresetName = "Fade",
            DurationBars = 8,
        });

        // 3. Orphaned item ids (no matching PlaylistTracks row) persist container-scoped with UseAsPairDefault = false.
        var orphanOut = Guid.NewGuid();
        var orphanIn = Guid.NewGuid();
        await repo.UpsertTransitionAsync(new PlaylistTrackTransition
        {
            PlaylistId = playlistId,
            OutgoingPlaylistTrackId = orphanOut,
            IncomingPlaylistTrackId = orphanIn,
            PresetName = "Rise",
            Type = TransitionType.FilterSweep,
            DurationBars = 8,
        });

        var allForPlaylist = await repo.GetTransitionsForPlaylistAsync(playlistId);
        Assert.Equal(4, allForPlaylist.Count);
        Assert.Equal("Blend", (await repo.GetTransitionAsync(pt1.Id, pt2.Id))!.PresetName);
        Assert.Equal("Melt", (await repo.GetTransitionAsync(pt3.Id, pt4.Id))!.PresetName);

        await using var verify = new AppDbContext();
        var stored = await verify.Transitions.AsNoTracking().ToListAsync();
        Assert.Equal(4, stored.Count);

        var repriseRows = stored.Where(t => t.OutgoingTrackHash == "hash-a" && t.IncomingTrackHash == "hash-b").ToList();
        Assert.Equal(2, repriseRows.Count);
        Assert.All(repriseRows, r => Assert.True(r.UseAsPairDefault));

        var nullHashRow = Assert.Single(stored, t => t.IncomingItemId == ptNoHash.Id);
        Assert.Null(nullHashRow.IncomingTrackHash);
        Assert.False(nullHashRow.UseAsPairDefault);

        var orphanRow = Assert.Single(stored, t => t.OutgoingItemId == orphanOut);
        Assert.Null(orphanRow.OutgoingTrackHash);
        Assert.False(orphanRow.UseAsPairDefault);
    }
}
