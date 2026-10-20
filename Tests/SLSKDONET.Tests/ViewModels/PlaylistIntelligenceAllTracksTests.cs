using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// 2026-10-20: the Library context panel's Playlist Intelligence said "No tracks in this playlist
/// yet" for All Tracks while the header showed 3,267 tracks. All Tracks is the Guid.Empty
/// pseudo-project backed by LibraryEntries, so the PlaylistTracks query found nothing.
/// </summary>
public sealed class PlaylistIntelligenceAllTracksTests : IDisposable
{
    private readonly string _dir;
    private readonly DbContextOptions<AppDbContext> _options;

    public PlaylistIntelligenceAllTracksTests()
    {
        var root = Path.GetDirectoryName(OrbitPaths.LibraryDbPath)!;
        Assert.Contains("orbit-tests", root); // never the user's library
        _dir = Path.Combine(root, "intel-all-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "library.db")};Pooling=False").Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    [Fact]
    public void AllTracks_ReadsTheWholeLibrary_NotPlaylistRows()
    {
        var allTracks = new PlaylistJob { Id = Guid.Empty, SourceTitle = "All Tracks", SourceType = "Global Library" };
        Assert.Equal(PlaylistIntelligenceViewModel.OverviewSource.AllLibrary, PlaylistIntelligenceViewModel.ResolveOverviewSource(allTracks, 0));

        var liked = new PlaylistJob { Id = Guid.NewGuid(), SourceTitle = "Liked Songs", SourceType = "Smart:Liked" };
        Assert.Equal(PlaylistIntelligenceViewModel.OverviewSource.Liked, PlaylistIntelligenceViewModel.ResolveOverviewSource(liked, 0));

        var playlist = new PlaylistJob { Id = Guid.NewGuid(), SourceTitle = "Set", SourceType = "User" };
        Assert.Equal(PlaylistIntelligenceViewModel.OverviewSource.Playlist, PlaylistIntelligenceViewModel.ResolveOverviewSource(playlist, 0));

        Assert.Equal(PlaylistIntelligenceViewModel.OverviewSource.InMemory, PlaylistIntelligenceViewModel.ResolveOverviewSource(playlist, 12));
        Assert.Equal(PlaylistIntelligenceViewModel.OverviewSource.None, PlaylistIntelligenceViewModel.ResolveOverviewSource(null, 0));
    }

    [Fact]
    public async Task LibraryOverviewQuery_ReturnsEveryEntry_AndLikedSubset()
    {
        await using (var db = new AppDbContext(_options))
        {
            for (int i = 0; i < 30; i++)
            {
                db.LibraryEntries.Add(new LibraryEntryEntity
                {
                    UniqueHash = "h" + i,
                    Artist = "Artist " + (i % 3),
                    Title = "T" + i,
                    FilePath = $"C:/music/{i}.flac",
                    DurationSeconds = 300,
                    BPM = 170 + i % 5,
                    MusicalKey = "8A",
                    Genres = i % 2 == 0 ? "Drum & Bass, Liquid Funk" : null,
                    IsLiked = i < 4,
                    AddedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        await using var read = new AppDbContext(_options);
        var all = await LibraryOverviewQuery.LoadAsync(read, likedOnly: false);
        var liked = await LibraryOverviewQuery.LoadAsync(read, likedOnly: true);

        Assert.Equal(30, all.Count);
        Assert.Equal(4, liked.Count);
        Assert.All(all, t => Assert.Equal(300_000, t.CanonicalDuration)); // seconds → ms for Duration
        Assert.Equal(300, all[0].Duration);
        Assert.Equal("Drum & Bass", PlaylistIntelligenceViewModel.OverviewGenre(all.First(t => t.Genres != null)));
    }
}
