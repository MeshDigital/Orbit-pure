using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class VirtualizedTrackCollectionSelectionTests
{
    [Fact]
    public async Task SelectionRange_LoadsEveryMissingPageAndRespectsBoundaries()
    {
        var sut = Build(trackCount: 25, pageSize: 10);
        await WaitUntil(() => sut.Count == 25);
        await sut.LoadPageAsync(0); // Reproduce Shift+End starting with only the first page loaded.

        var batches = new List<int>();
        var selected = new List<PlaylistTrackViewModel>();
        await foreach (var page in sut.LoadRangeForSelectionAsync(2, 24))
        {
            batches.Add(page.Count);
            selected.AddRange(page);
        }

        Assert.Equal(new[] { 8, 10, 5 }, batches);
        Assert.Equal(23, selected.Count);
        Assert.Equal(
            Enumerable.Range(2, 23).Select(index => $"T{index}"),
            selected.Select(track => track.Model.Title));
    }

    private static VirtualizedTrackCollection Build(int trackCount, int pageSize)
    {
        var playlistId = Guid.NewGuid();
        var library = new Mock<ILibraryService>();
        library.Setup(s => s.GetTrackCountAsync(playlistId, null, null, null, null, null))
            .ReturnsAsync(trackCount);
        library.Setup(s => s.GetPagedPlaylistTracksAsync(
                playlistId, It.IsAny<int>(), It.IsAny<int>(), null, null, null, null,
                TrackSortColumn.Default, false, null))
            .ReturnsAsync((Guid _, int skip, int take, string? _, bool? _, IEnumerable<string>? _,
                string? _, TrackSortColumn _, bool _, string? _) =>
                Enumerable.Range(skip, Math.Max(0, Math.Min(take, trackCount - skip)))
                    .Select(index => new PlaylistTrack
                    {
                        Id = Guid.NewGuid(),
                        PlaylistId = playlistId,
                        TrackUniqueHash = $"h{index}",
                        Artist = "A",
                        Title = $"T{index}"
                    })
                    .ToList());

        return new VirtualizedTrackCollection(
            NullLogger<VirtualizedTrackCollection>.Instance,
            library.Object,
            new EventBusService(),
            new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient()),
            playlistId,
            pageSize: pageSize);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(5);
    }
}
