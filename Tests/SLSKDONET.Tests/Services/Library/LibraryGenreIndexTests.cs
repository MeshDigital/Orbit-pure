using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Services.Library;

/// <summary>
/// Regression tests for Epic #561 / #562 (GV1: LibraryGenreIndex + JSON-array genre unwrapping).
/// </summary>
[Collection(NonParallelCollection.Name)]
public sealed class LibraryGenreIndexTests
{
    [Fact]
    public void SplitGenreText_UnwrapsJsonArray_SameAsCommaText()
    {
        var fromJson = GenreTaxonomy.MapFreeTextAll("[\"Techno\", \"Liquid Drum & Bass\"]");
        var fromCsv = GenreTaxonomy.MapFreeTextAll("Techno, Liquid Drum & Bass");

        Assert.Equal(fromCsv.Count, fromJson.Count);
        Assert.Equal(GenreBucket.Techno, fromJson[0].Bucket);
        Assert.Equal(GenreBucket.DrumAndBass, fromJson[1].Bucket);

        var popJson = GenreTaxonomy.MapFreeTextAll("[\"Pop\"]");
        Assert.Single(popJson);
        Assert.Equal("Pop", popJson[0].Subgenre);
    }

    [Fact]
    public async Task BuildAsync_CountsSumToLibraryTotal_EachTrackUnderOneNode_AndUserGenreBeatsAudio()
    {
        using var scope = new FreshDatabaseScope();
        IDbContextFactory<AppDbContext> factory = new SimpleDbContextFactory();

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();

            db.LibraryEntries.AddRange(
                new LibraryEntryEntity { UniqueHash = "h1", Artist = "A", Title = "T1", FilePath = "/t1.flac", Genres = "[\"Liquid Drum & Bass\"]" },
                new LibraryEntryEntity { UniqueHash = "h2", Artist = "B", Title = "T2", FilePath = "/t2.flac", Genres = "Techno" },
                new LibraryEntryEntity { UniqueHash = "h3", Artist = "C", Title = "T3", FilePath = "/t3.flac", Genres = "Pop" },
                new LibraryEntryEntity { UniqueHash = "h4", Artist = "D", Title = "T4", FilePath = "/t4.flac", Genres = "" });

            // User provenance on h2 overrides strong House audio opinion
            db.MetadataProvenance.Add(new MetadataProvenanceEntity
            {
                TrackUniqueHash = "h2",
                Field = "genre",
                Source = "user",
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.AudioFeatures.Add(new AudioFeaturesEntity
            {
                TrackUniqueHash = "h2",
                DetectedSubGenre = "House",
                SubGenreConfidence = 0.68f
            });

            await db.SaveChangesAsync();
        }

        using var index = new LibraryGenreIndex(factory, eventBus: null, NullLogger<LibraryGenreIndex>.Instance);
        await index.BuildAsync();

        Assert.True(index.IsReady);
        Assert.Null(index.LastError);

        var tree = index.GetTree();
        Assert.Equal(4, tree.Sum(n => n.TrackCount));

        // h1: JSON array mapped to DrumAndBass
        Assert.Equal(GenreBucket.DrumAndBass, index.GetKey("h1")?.Bucket);
        // h2: user-set Techno beat House audio
        Assert.Equal(GenreBucket.Techno, index.GetKey("h2")?.Bucket);
        // h3: Other / Pop
        Assert.Equal(GenreBucket.Other, index.GetKey("h3")?.Bucket);
        Assert.Equal("Pop", index.GetKey("h3")?.Subgenre);
        // h4: Unclassified
        Assert.Equal(GenreKey.Unclassified, index.GetKey("h4"));

        // Bucket-level GetHashes returns all tracks in that bucket
        var dnbHashes = index.GetHashes(new GenreKey(GenreBucket.DrumAndBass));
        Assert.Contains("h1", dnbHashes);
        Assert.Single(dnbHashes);
    }

    [Fact]
    public async Task RefreshTracksAsync_ReKeysOnlyAffectedTrack()
    {
        using var scope = new FreshDatabaseScope();
        IDbContextFactory<AppDbContext> factory = new SimpleDbContextFactory();

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.LibraryEntries.AddRange(
                new LibraryEntryEntity { UniqueHash = "t1", Artist = "A", Title = "One", FilePath = "/1.flac", Genres = "Techno" },
                new LibraryEntryEntity { UniqueHash = "t2", Artist = "B", Title = "Two", FilePath = "/2.flac", Genres = "House" });
            await db.SaveChangesAsync();
        }

        using var index = new LibraryGenreIndex(factory, eventBus: null, NullLogger<LibraryGenreIndex>.Instance);
        await index.BuildAsync();
        Assert.Equal(GenreBucket.Techno, index.GetKey("t1")?.Bucket);

        var changedCount = 0;
        index.Changed += (_, _) => changedCount++;

        await using (var db = await factory.CreateDbContextAsync())
        {
            var e1 = await db.LibraryEntries.FirstAsync(e => e.UniqueHash == "t1");
            e1.Genres = "Dubstep";
            await db.SaveChangesAsync();
        }

        await index.RefreshTracksAsync(new[] { "t1" });

        Assert.Equal(GenreBucket.Dubstep, index.GetKey("t1")?.Bucket);
        Assert.Equal(GenreBucket.House, index.GetKey("t2")?.Bucket);
        Assert.Equal(1, changedCount);
    }

    private sealed class SimpleDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
    }
}
