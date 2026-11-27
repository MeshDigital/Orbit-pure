using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Performance;

/// <summary>
/// Local-only performance regression gates for PF1 (#607).
/// Excluded from noisy shared CI runners via <c>Category=Perf</c>; run locally with:
/// <c>dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -c Release --filter "Category=Perf"</c>.
/// </summary>
[Collection(NonParallelCollection.Name)]
public sealed class LibraryPerformanceBudgetTests
{
    private const int TrackCount = 5_000;

    [Fact, Trait("Category", "Perf")]
    public async Task LibraryHotPaths_MeetPf1Budgets_On5kSyntheticLibrary()
    {
        using var scope = new FreshDatabaseScope();
        await SeedSyntheticLibraryAsync(TrackCount);

        var repo = new TrackRepository(NullLogger<TrackRepository>.Instance);
        var factory = new AppDbContextFactory();

        // Warm up EF query plan cache once.
        _ = await repo.GetPagedAllTracksAsync(0, 100);

        // 1. First Library page query (budget: <= 30 ms on 5k)
        var pageSw = Stopwatch.StartNew();
        var page = await repo.GetPagedAllTracksAsync(0, 100);
        pageSw.Stop();
        Assert.Equal(100, page.Count);
        Assert.True(pageSw.Elapsed.TotalMilliseconds <= 30.0,
            $"First Library page took {pageSw.Elapsed.TotalMilliseconds:F1} ms (budget <= 30 ms on 5k).");

        // 2. Search keystroke -> results (budget: <= 25 ms on 5k)
        var searchSw = Stopwatch.StartNew();
        var searchRows = await repo.GetPagedAllTracksAsync(0, 100, filter: "cal");
        searchSw.Stop();
        Assert.NotEmpty(searchRows);
        Assert.True(searchSw.Elapsed.TotalMilliseconds <= 25.0,
            $"Search keystroke query took {searchSw.Elapsed.TotalMilliseconds:F1} ms (budget <= 25 ms on 5k).");

        // 3. LibraryGenreIndex cold build (budget: <= 150 ms on 5k)
        using var genreIndex = new LibraryGenreIndex(factory, eventBus: null, NullLogger<LibraryGenreIndex>.Instance);
        var genreSw = Stopwatch.StartNew();
        await genreIndex.BuildAsync();
        genreSw.Stop();
        Assert.True(genreIndex.IsReady);
        Assert.True(genreSw.Elapsed.TotalMilliseconds <= 150.0,
            $"LibraryGenreIndex.BuildAsync took {genreSw.Elapsed.TotalMilliseconds:F1} ms (budget <= 150 ms on 5k).");
    }

    private static async Task SeedSyntheticLibraryAsync(int count)
    {
        await using (var init = new AppDbContext())
        {
            await init.Database.EnsureCreatedAsync();
        }

        string[] artists = { "Calibre", "Alix Perez", "Bicep", "Overmono", "Dax J", "Peggy Gou" };
        string[] genres = { "Drum & Bass, Liquid DnB", "Techno, Peak Time Techno", "House, Deep House", "Dubstep, Deep Dubstep" };
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using var ctx = new AppDbContext();
        ctx.ChangeTracker.AutoDetectChangesEnabled = false;

        var entries = new List<LibraryEntryEntity>(count);
        var features = new List<AudioFeaturesEntity>(count);
        for (int i = 0; i < count; i++)
        {
            var hash = $"perf_{i:D6}";
            var artist = artists[i % artists.Length];
            var genre = genres[i % genres.Length];
            entries.Add(new LibraryEntryEntity
            {
                UniqueHash = hash,
                Id = Guid.NewGuid(),
                Artist = artist,
                Title = $"Track {i}",
                Album = "Perf EP",
                FilePath = $@"C:\Music\{hash}.flac",
                AvailabilityState = TrackAvailabilityState.Ready,
                Bitrate = 1411,
                DurationSeconds = 300,
                Format = "flac",
                AddedAt = baseTime.AddSeconds(i),
                LastUsedAt = baseTime.AddSeconds(i),
                Genres = genre,
                BPM = 174,
                MusicalKey = "8A"
            });
            features.Add(new AudioFeaturesEntity
            {
                Id = Guid.NewGuid(),
                TrackUniqueHash = hash,
                TrackDuration = 300,
                Bpm = 174,
                CamelotKey = "8A",
                DetectedSubGenre = "Liquid DnB",
                ElectronicSubgenre = "Drum & Bass",
                ElectronicSubgenreConfidence = 0.85f
            });
        }

        ctx.LibraryEntries.AddRange(entries);
        ctx.AudioFeatures.AddRange(features);
        await ctx.SaveChangesAsync();

        await ctx.Database.ExecuteSqlRawAsync("""
            CREATE VIRTUAL TABLE IF NOT EXISTS LibraryEntriesFts USING fts5(
                Artist, Title, Album, Key, UniqueHash UNINDEXED
            );
            INSERT INTO LibraryEntriesFts(rowid, Artist, Title, Album, Key, UniqueHash)
            SELECT rowid, Artist, Title, Album, COALESCE(MusicalKey, ''), UniqueHash
            FROM LibraryEntries;
            """);
    }

    private sealed class AppDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
    }
}
