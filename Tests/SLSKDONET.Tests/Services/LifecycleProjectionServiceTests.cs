using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class LifecycleProjectionServiceTests
{
    [Fact]
    public async Task ComputeMetricsAsync_ProjectsConsistentLifecycleCounts()
    {
        var existingFile = Path.GetTempFileName();
        var deletedFile = existingFile + ".missing";

        try
        {
            var library = new Mock<ILibraryService>();
            library.Setup(s => s.LoadAllLibraryEntriesAsync()).ReturnsAsync(new List<LibraryEntry>
            {
                new()
                {
                    UniqueHash = "indexed-hash",
                    FilePath = existingFile,
                    AddedAt = DateTime.UtcNow,
                },
                new()
                {
                    UniqueHash = "stale-hash",
                    FilePath = deletedFile,
                    AddedAt = DateTime.UtcNow,
                },
            });

            library.Setup(s => s.GetAllPlaylistTracksAsync()).ReturnsAsync(new List<PlaylistTrack>
            {
                new()
                {
                    Status = TrackStatus.Missing,
                },
                new()
                {
                    Status = TrackStatus.Pending,
                },
                new()
                {
                    Status = TrackStatus.Downloaded,
                    ResolvedFilePath = existingFile,
                    TrackUniqueHash = "not-indexed-hash",
                },
                new()
                {
                    Status = TrackStatus.Downloaded,
                    ResolvedFilePath = existingFile,
                    TrackUniqueHash = "indexed-hash",
                },
            });

            var sut = new LifecycleProjectionService(library.Object);

            var metrics = await sut.ComputeMetricsAsync();

            Assert.Equal(1, metrics.PhysicalOnDisk);
            Assert.Equal(2, metrics.IndexedCatalog);
            Assert.Equal(1, metrics.StaleIndexed);
            Assert.Equal(1, metrics.IngestionBacklog);
            Assert.Equal(2, metrics.DesiredDownloads);
        }
        finally
        {
            if (File.Exists(existingFile))
            {
                File.Delete(existingFile);
            }
        }
    }

    [Fact]
    public void ApplyFileIngestionCompleted_KeepsBacklogNonNegative_AndRebalancesCounts()
    {
        var sut = new LifecycleProjectionService(new Mock<ILibraryService>().Object);

        var next = sut.ApplyFileIngestionCompleted(new LifecycleMetrics(
            PhysicalOnDisk: 0,
            IndexedCatalog: 0,
            StaleIndexed: 0,
            IngestionBacklog: 0,
            DesiredDownloads: 3));

        Assert.Equal(1, next.PhysicalOnDisk);
        Assert.Equal(1, next.IndexedCatalog);
        Assert.Equal(0, next.StaleIndexed);
        Assert.Equal(0, next.IngestionBacklog);
        Assert.Equal(3, next.DesiredDownloads);
    }

    [Fact]
    public void ApplyFileMissingDetected_DecrementsPhysical_AndIncreasesStaleViaNormalization()
    {
        var sut = new LifecycleProjectionService(new Mock<ILibraryService>().Object);

        var next = sut.ApplyFileMissingDetected(new LifecycleMetrics(
            PhysicalOnDisk: 4,
            IndexedCatalog: 6,
            StaleIndexed: 2,
            IngestionBacklog: 1,
            DesiredDownloads: 5));

        Assert.Equal(3, next.PhysicalOnDisk);
        Assert.Equal(6, next.IndexedCatalog);
        Assert.Equal(3, next.StaleIndexed);
        Assert.Equal(1, next.IngestionBacklog);
        Assert.Equal(5, next.DesiredDownloads);
    }
}
