using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Repositories;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.Library;

/// <summary>
/// Regression tests for Epic #561: GV3 (#564), GV4 (#565), GV5 (#566), and GV6 (#567).
/// </summary>
[Collection(NonParallelCollection.Name)]
public sealed class GenreViewEpicTests
{
    private sealed class SimpleDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new AppDbContext();
    }

    // ── GV3 (#564): GENRES tree VM (counts, Other overflow, selection sync) ──

    [Fact]
    public async Task GenresSection_OtherCapsAtTop10WithOverflowRow_AndUnclassifiedSitsLast()
    {
        using var scope = new FreshDatabaseScope();
        IDbContextFactory<AppDbContext> factory = new SimpleDbContextFactory();

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();

            // 12 distinct non-club tags under Other so Other exceeds OtherTopLimit (10)
            var otherTags = new[]
            {
                "Pop", "Rock", "Jazz", "Funk", "Soul", "Ambient",
                "Reggae", "Disco", "Blues", "Classical", "Folk", "Punk"
            };
            for (int i = 0; i < otherTags.Length; i++)
            {
                db.LibraryEntries.Add(new LibraryEntryEntity
                {
                    UniqueHash = $"other-{i}",
                    Artist = $"Artist {i}",
                    Title = $"Track {i}",
                    FilePath = $"/other-{i}.flac",
                    Genres = otherTags[i]
                });
            }

            // 2 Drum and Bass tracks + 1 Unclassified
            db.LibraryEntries.Add(new LibraryEntryEntity
            {
                UniqueHash = "dnb-1",
                Artist = "Calibre",
                Title = "Even If",
                FilePath = "/dnb1.flac",
                Genres = "Liquid Drum & Bass"
            });
            db.LibraryEntries.Add(new LibraryEntryEntity
            {
                UniqueHash = "dnb-2",
                Artist = "Noisia",
                Title = "Stigma",
                FilePath = "/dnb2.flac",
                Genres = "Neurofunk"
            });
            db.LibraryEntries.Add(new LibraryEntryEntity
            {
                UniqueHash = "unclass-1",
                Artist = "Unknown",
                Title = "Mystery",
                FilePath = "/u1.flac",
                Genres = ""
            });
            await db.SaveChangesAsync();
        }

        using var index = new LibraryGenreIndex(factory, eventBus: null, NullLogger<LibraryGenreIndex>.Instance);
        await index.BuildAsync();

        GenreKey? activated = null;
        using var section = new LibraryGenresSectionViewModel(
            index,
            tracks: null,
            onGenreActivated: k => activated = k);

        Assert.False(section.IsLoading);
        Assert.False(section.HasError);
        Assert.True(section.HasNodes);
        Assert.Equal(15, section.TotalTrackCount);

        // Unclassified sits last
        Assert.True(section.Nodes[^1].IsUnclassified);
        Assert.Equal(1, section.Nodes[^1].TrackCount);

        // Other bucket shows top 10 + 1 synthetic "2 more…" row
        var otherNode = section.Nodes.Single(n => !n.IsUnclassified && n.Key.Bucket == GenreBucket.Other);
        Assert.Equal(12, otherNode.TrackCount);
        Assert.Equal(LibraryGenresSectionViewModel.OtherTopLimit + 1, otherNode.Children.Count);
        var overflowRow = otherNode.Children[^1];
        Assert.True(overflowRow.IsOverflowRow);
        Assert.Equal("2 more…", overflowRow.DisplayName);

        // Clicking overflow expands all 12 children
        overflowRow.ActivateCommand.Execute(null);
        otherNode = section.Nodes.Single(n => !n.IsUnclassified && n.Key.Bucket == GenreBucket.Other);
        Assert.Equal(12, otherNode.Children.Count);
        Assert.All(otherNode.Children, c => Assert.False(c.IsOverflowRow));

        // Clicking a node activates it
        var dnbNode = section.Nodes.Single(n => n.Key.Bucket == GenreBucket.DrumAndBass);
        dnbNode.ActivateCommand.Execute(null);
        Assert.Equal(dnbNode.Key, activated);

        // SyncSelection highlights child and expands parent bucket, clearing deselects
        var firstDnbChild = dnbNode.Children[0];
        section.SyncSelection(firstDnbChild.Key);
        Assert.True(firstDnbChild.IsSelected);
        Assert.True(dnbNode.IsExpanded);

        section.SyncSelection(null);
        Assert.False(firstDnbChild.IsSelected);
        Assert.False(dnbNode.IsSelected);
    }

    // ── GV4 (#565): Repository ordered view matches paged grid order ──

    [Fact]
    public async Task TrackRepository_GetOrderedAllTracks_AndPlaylistTracks_MatchPagedOrderAndFilters()
    {
        using var scope = new FreshDatabaseScope();
        var playlistId = Guid.NewGuid();

        await using (var db = new AppDbContext())
        {
            await db.Database.EnsureCreatedAsync();

            db.LibraryEntries.AddRange(
                new LibraryEntryEntity { UniqueHash = "h1", Artist = "Zebra", Title = "Alpha", FilePath = "/1.flac", BPM = 174 },
                new LibraryEntryEntity { UniqueHash = "h2", Artist = "Apex", Title = "Beta", FilePath = "/2.flac", BPM = 170 },
                new LibraryEntryEntity { UniqueHash = "h3", Artist = "Mefjus", Title = "Gamma", FilePath = "", BPM = 172 });

            db.PlaylistTracks.AddRange(
                new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = "h1", Artist = "Zebra", Title = "Alpha", ResolvedFilePath = "/1.flac", Status = TrackStatus.Downloaded, SortOrder = 2, TrackNumber = 2, BPM = 174 },
                new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = "h2", Artist = "Apex", Title = "Beta", ResolvedFilePath = "/2.flac", Status = TrackStatus.Downloaded, SortOrder = 1, TrackNumber = 1, BPM = 170 },
                new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = "h3", Artist = "Mefjus", Title = "Gamma", ResolvedFilePath = "", Status = TrackStatus.Missing, SortOrder = 0, TrackNumber = 0, BPM = 172 });

            await db.SaveChangesAsync();
        }

        var repo = new TrackRepository(NullLogger<TrackRepository>.Instance);

        // All Tracks sorted by Artist ascending, downloadedOnly = true, filtered by hash set {h1, h2}
        var pagedAll = await repo.GetPagedAllTracksAsync(0, 50, downloadedOnly: true, hashFilter: new[] { "h1", "h2" }, sortColumn: TrackSortColumn.Artist, sortDescending: false);
        var orderedAll = await repo.GetOrderedAllTracksAsync(downloadedOnly: true, hashFilter: new[] { "h1", "h2" }, sortColumn: TrackSortColumn.Artist, sortDescending: false);
        Assert.Equal(pagedAll.Select(t => t.TrackUniqueHash), orderedAll.Select(t => t.TrackUniqueHash));
        Assert.Equal(new[] { "h2", "h1" }, orderedAll.Select(t => t.TrackUniqueHash).ToArray());

        // Playlist sorted by BPM descending, downloadedOnly = true
        var pagedPl = await repo.GetPagedPlaylistTracksAsync(playlistId, 0, 50, downloadedOnly: true, sortColumn: TrackSortColumn.Bpm, sortDescending: true);
        var orderedPl = await repo.GetOrderedPlaylistTracksAsync(playlistId, downloadedOnly: true, sortColumn: TrackSortColumn.Bpm, sortDescending: true);
        Assert.Equal(pagedPl.Select(t => t.TrackUniqueHash), orderedPl.Select(t => t.TrackUniqueHash));
        Assert.Equal(new[] { "h1", "h2" }, orderedPl.Select(t => t.TrackUniqueHash).ToArray());
    }

    // ── GV5 (#566): Smart crate live genre evaluation + legacy SubGenre migration ──

    [Fact]
    public async Task SmartCrateService_GenreKeyCrate_FollowsGenreEdits_AndMigratesLegacySubGenre()
    {
        using var scope = new FreshDatabaseScope();
        IDbContextFactory<AppDbContext> factory = new SimpleDbContextFactory();

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.LibraryEntries.AddRange(
                new LibraryEntryEntity { UniqueHash = "g1", Artist = "Calibre", Title = "Drop It Down", FilePath = "/g1.flac", Genres = "Liquid Drum & Bass" },
                new LibraryEntryEntity { UniqueHash = "g2", Artist = "Surgeon", Title = "Badger Bite", FilePath = "/g2.flac", Genres = "Techno" });
            await db.SaveChangesAsync();
        }

        using var index = new LibraryGenreIndex(factory, eventBus: null, NullLogger<LibraryGenreIndex>.Instance);
        await index.BuildAsync();

        var crateService = new SmartCrateService(factory, NullLogger<SmartCrateService>.Instance, index);

        // Live GenreKey crate
        var rules = new SmartCrateRules();
        rules.SetGenreKey(new GenreKey(GenreBucket.DrumAndBass));
        var crate = await crateService.CreateCrateAsync("DnB Live", rules);

        var initialMatches = await crateService.GetMatchingTrackIdsAsync(crate);
        Assert.Equal(new[] { "g1" }, initialMatches.ToArray());

        // Edit g2's genre to Drum & Bass and refresh index -> crate members update live
        await using (var db = await factory.CreateDbContextAsync())
        {
            var e2 = await db.LibraryEntries.FirstAsync(e => e.UniqueHash == "g2");
            e2.Genres = "Neurofunk";
            await db.SaveChangesAsync();
        }
        await index.RefreshTracksAsync(new[] { "g2" });

        var updatedMatches = await crateService.GetMatchingTrackIdsAsync(crate);
        Assert.Equal(2, updatedMatches.Count);
        Assert.Contains("g1", updatedMatches);
        Assert.Contains("g2", updatedMatches);

        // Legacy SubGenre migration
        var legacyRules = new SmartCrateRules { SubGenre = "Liquid Drum & Bass" };
        Assert.True(SmartCrateService.TryMigrateLegacySubGenre(legacyRules, out var migratedKey));
        Assert.NotNull(migratedKey);
        Assert.Equal(GenreBucket.DrumAndBass, migratedKey!.Value.Bucket);

        var legacyCrate = await crateService.CreateCrateAsync("Legacy Liquid", legacyRules);
        var legacyMatches = await crateService.GetMatchingTrackIdsAsync(legacyCrate);
        Assert.Contains("g1", legacyMatches);

        // Unmapped / empty legacy SubGenre stays unmigrated
        var unmappedRules = new SmartCrateRules { SubGenre = "   " };
        Assert.False(SmartCrateService.TryMigrateLegacySubGenre(unmappedRules, out _));
    }

    // ── GV6 (#567): Genre Galaxy counts match LibraryGenreIndex tree ──

    [Fact]
    public async Task DashboardService_BuildTopGenresFromIndex_MatchesLibraryTreeCountsAndCapsAt8()
    {
        using var scope = new FreshDatabaseScope();
        IDbContextFactory<AppDbContext> factory = new SimpleDbContextFactory();

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.LibraryEntries.AddRange(
                new LibraryEntryEntity { UniqueHash = "t1", Artist = "A", Title = "1", FilePath = "/1.flac", Genres = "Liquid Drum & Bass" },
                new LibraryEntryEntity { UniqueHash = "t2", Artist = "A", Title = "2", FilePath = "/2.flac", Genres = "Neurofunk" },
                new LibraryEntryEntity { UniqueHash = "t3", Artist = "B", Title = "3", FilePath = "/3.flac", Genres = "Techno" },
                new LibraryEntryEntity { UniqueHash = "t4", Artist = "C", Title = "4", FilePath = "/4.flac", Genres = "Pop" },
                new LibraryEntryEntity { UniqueHash = "t5", Artist = "D", Title = "5", FilePath = "/5.flac", Genres = "" });
            await db.SaveChangesAsync();
        }

        using var index = new LibraryGenreIndex(factory, eventBus: null, NullLogger<LibraryGenreIndex>.Instance);
        await index.BuildAsync();

        var tree = index.GetTree();
        var planets = DashboardService.BuildTopGenresFromIndex(index, maxPlanets: 8);

        Assert.True(planets.Count <= 8);
        Assert.DoesNotContain(planets, p => p.IsUnclassified);

        // Every planet's count equals the exact node count in LibraryGenreIndex.GetTree() for that GenreKey
        var dnbPlanet = planets.Single(p => p.ToGenreKey() == new GenreKey(GenreBucket.DrumAndBass));
        var dnbTree = tree.Single(n => n.Key == new GenreKey(GenreBucket.DrumAndBass));
        Assert.Equal(dnbTree.TrackCount, dnbPlanet.Count);

        var popPlanet = planets.Single(p => p.ToGenreKey() == new GenreKey(GenreBucket.Other, "Pop"));
        var popTree = tree.Single(n => !n.Key.IsUnclassified && n.Key.Bucket == GenreBucket.Other).Children.Single(c => c.Key == new GenreKey(GenreBucket.Other, "Pop"));
        Assert.Equal(popTree.TrackCount, popPlanet.Count);

        // JSON round-trip preserves GenreKey
        var json = JsonSerializer.Serialize(planets);
        var roundTripped = JsonSerializer.Deserialize<List<TopGenreSnapshotEntry>>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(dnbPlanet.ToGenreKey(), roundTripped![0].ToGenreKey());
    }
}
