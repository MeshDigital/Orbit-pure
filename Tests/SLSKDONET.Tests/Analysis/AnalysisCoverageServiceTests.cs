using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Analysis;

public sealed class AnalysisCoverageServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), $"orbit-coverage-{Guid.NewGuid():N}");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Factory _factory;

    public AnalysisCoverageServiceTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"analysis-coverage-{Guid.NewGuid():N}")
            .Options;
        _factory = new Factory(_options);
    }

    [Fact]
    public async Task LibraryCoverage_RequiresAvailableStateAndAnExistingAudioFile()
    {
        var present = CreateAudioFile();
        var alsoPresent = CreateAudioFile();
        await using (var db = new AppDbContext(_options))
        {
            db.LibraryEntries.AddRange(
                new LibraryEntryEntity
                {
                    UniqueHash = "present", FilePath = present,
                    AvailabilityState = TrackAvailabilityState.Ready,
                },
                new LibraryEntryEntity
                {
                    UniqueHash = "missing-file", FilePath = Path.Combine(_tempDirectory, "missing.flac"),
                    AvailabilityState = TrackAvailabilityState.Ready,
                },
                new LibraryEntryEntity
                {
                    UniqueHash = "ghost", FilePath = present,
                    AvailabilityState = TrackAvailabilityState.Ghost,
                },
                new LibraryEntryEntity
                {
                    UniqueHash = "local", FilePath = alsoPresent,
                    AvailabilityState = TrackAvailabilityState.LocalUnanalyzed,
                });
            await db.SaveChangesAsync();
        }

        var service = CreateService();
        var coverage = await service.GetLibraryCoverageAsync();

        Assert.Equal(2, coverage.TotalTracks);
        Assert.Equal(
            new[] { "local", "present" },
            coverage.Tracks.Select(t => t.TrackHash).OrderBy(h => h).ToArray());
    }

    [Fact]
    public async Task PlaylistCoverage_UsesItsOwnRowsAndVerifiesTheirResolvedPaths()
    {
        var playlistId = Guid.NewGuid();
        var otherPlaylistId = Guid.NewGuid();
        var present = CreateAudioFile();
        await using (var db = new AppDbContext(_options))
        {
            db.PlaylistTracks.AddRange(
                new PlaylistTrackEntity
                {
                    PlaylistId = playlistId, TrackUniqueHash = "selected",
                    Status = TrackStatus.Missing, AvailabilityState = TrackAvailabilityState.Ready,
                    ResolvedFilePath = present,
                },
                new PlaylistTrackEntity
                {
                    PlaylistId = playlistId, TrackUniqueHash = "stale-path",
                    Status = TrackStatus.Downloaded, AvailabilityState = TrackAvailabilityState.Ready,
                    ResolvedFilePath = Path.Combine(_tempDirectory, "deleted.flac"),
                },
                new PlaylistTrackEntity
                {
                    PlaylistId = playlistId, TrackUniqueHash = "not-offline",
                    Status = TrackStatus.Missing, AvailabilityState = TrackAvailabilityState.Ghost,
                    ResolvedFilePath = present,
                },
                new PlaylistTrackEntity
                {
                    PlaylistId = otherPlaylistId, TrackUniqueHash = "other-playlist",
                    Status = TrackStatus.Downloaded, AvailabilityState = TrackAvailabilityState.Ready,
                    ResolvedFilePath = present,
                });
            await db.SaveChangesAsync();
        }

        var service = CreateService();
        var coverage = await service.GetPlaylistCoverageAsync(playlistId, "Selected");

        var track = Assert.Single(coverage.Tracks);
        Assert.Equal("selected", track.TrackHash);
        Assert.Equal("Selected", coverage.PlaylistName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private AnalysisCoverageService CreateService() =>
        new(_factory, NullLogger<AnalysisCoverageService>.Instance);

    private string CreateAudioFile()
    {
        var path = Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.flac");
        File.WriteAllText(path, "test audio placeholder");
        return path;
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public Factory(DbContextOptions<AppDbContext> options) => _options = options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
