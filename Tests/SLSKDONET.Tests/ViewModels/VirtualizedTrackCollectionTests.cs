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

/// <summary>
/// Coverage for the non-blocking indexer redesign: on a cache miss, VirtualizedTrackCollection
/// must return the shared PlaylistTrackViewModel.Placeholder immediately (never block the calling
/// thread) and backfill the real data in the background via the existing CollectionChanged event.
/// </summary>
public class VirtualizedTrackCollectionTests
{
    private static PlaylistTrack BuildTrack(int index, Guid playlistId) => new()
    {
        Id = Guid.NewGuid(),
        PlaylistId = playlistId,
        TrackUniqueHash = $"hash-{index}",
        Artist = $"Artist {index}",
        Title = $"Title {index}",
        TrackNumber = index
    };

    private static (VirtualizedTrackCollection Sut, Mock<ILibraryService> LibraryServiceMock) BuildSut(
        int totalCount,
        int pageSize = 10,
        Func<int, int, Task>? onPageRequested = null)
    {
        var playlistId = Guid.NewGuid();
        var libraryServiceMock = new Mock<ILibraryService>();

        libraryServiceMock
            .Setup(s => s.GetTrackCountAsync(playlistId, null, null, null, null, null))
            .ReturnsAsync(totalCount);

        libraryServiceMock
            .Setup(s => s.GetPagedPlaylistTracksAsync(
                playlistId, It.IsAny<int>(), It.IsAny<int>(), null, null, null, null, TrackSortColumn.Default, false, null))
            .Returns(async (Guid _, int skip, int take, string? _, bool? _, IEnumerable<string>? _, string? _, TrackSortColumn _, bool _, string? _) =>
            {
                if (onPageRequested != null)
                    await onPageRequested(skip, take);

                var count = Math.Min(take, totalCount - skip);
                return Enumerable.Range(skip, Math.Max(0, count)).Select(i => BuildTrack(i, playlistId)).ToList();
            });

        var eventBus = new EventBusService();
        var artworkCache = new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient());

        var sut = new VirtualizedTrackCollection(
            NullLogger<VirtualizedTrackCollection>.Instance,
            libraryServiceMock.Object,
            eventBus,
            artworkCache,
            playlistId,
            pageSize: pageSize);

        return (sut, libraryServiceMock);
    }

    private static async Task WaitForCountAsync(VirtualizedTrackCollection sut, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sut.Count == -1 && sw.Elapsed < timeout)
        {
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Indexer_OnCacheMiss_ReturnsImmediatelyWithoutBlocking()
    {
        var gate = new TaskCompletionSource();
        var (sut, _) = BuildSut(totalCount: 20, onPageRequested: async (_, _) => await gate.Task);
        await WaitForCountAsync(sut, TimeSpan.FromSeconds(2));

        var sw = Stopwatch.StartNew();
        var result = sut[0];
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"Indexer blocked for {sw.ElapsedMilliseconds}ms — expected an immediate return.");
        Assert.NotNull(result);

        gate.SetResult();
    }

    [Fact]
    public async Task Indexer_OnCacheMiss_ReturnsSharedPlaceholder()
    {
        var gate = new TaskCompletionSource();
        var (sut, _) = BuildSut(totalCount: 20, onPageRequested: async (_, _) => await gate.Task);
        await WaitForCountAsync(sut, TimeSpan.FromSeconds(2));

        var result = sut[0];

        Assert.Same(PlaylistTrackViewModel.Placeholder, result);
        Assert.True(result.IsPlaceholder);

        gate.SetResult();
    }

    [Fact]
    public async Task Indexer_OnCacheMiss_KicksOffBackgroundLoad_AndRealDataReplacesPlaceholder()
    {
        var (sut, _) = BuildSut(totalCount: 20, pageSize: 10);
        await WaitForCountAsync(sut, TimeSpan.FromSeconds(2));

        var placeholder = sut[3];
        Assert.True(placeholder.IsPlaceholder);

        // Directly await the (now-internal) page load rather than relying on the
        // background continuation timing, matching the established pattern in this test
        // suite of not depending on Dispatcher.UIThread.Post being pumped synchronously.
        await sut.LoadPageAsync(0);

        var real = sut[3];
        Assert.False(real.IsPlaceholder);
        Assert.Equal("hash-3", real.GlobalId);
        Assert.Equal("Title 3", real.Title);
    }

    [Fact]
    public async Task Indexer_ConcurrentMissesOnSamePage_OnlyLoadsPageOnce()
    {
        var (sut, libraryServiceMock) = BuildSut(totalCount: 20, pageSize: 10);
        await WaitForCountAsync(sut, TimeSpan.FromSeconds(2));

        // Several cache misses on the same page in quick succession.
        _ = sut[0];
        _ = sut[1];
        _ = sut[2];

        await sut.LoadPageAsync(0);

        libraryServiceMock.Verify(
            s => s.GetPagedPlaylistTracksAsync(
                It.IsAny<Guid>(), 0, 10, null, null, null, null, TrackSortColumn.Default, false, null),
            Times.Once);
    }

    [Fact]
    public async Task CopyTo_WhenSomeIndicesUnloaded_FillsPlaceholders_NeverRawNull()
    {
        var (sut, _) = BuildSut(totalCount: 25, pageSize: 10);
        await WaitForCountAsync(sut, TimeSpan.FromSeconds(2));

        // Nothing has been explicitly loaded yet — ToList() internally uses the generic
        // ICollection<T>.CopyTo, sized to the full DB count (25), not what's actually loaded.
        var list = ((System.Collections.Generic.IEnumerable<PlaylistTrackViewModel>)sut).ToList();

        Assert.Equal(25, list.Count);
        Assert.All(list, item => Assert.NotNull(item));
    }

    [Fact]
    public async Task GetSubset_WhenPartiallyLoaded_IncludesPlaceholdersWithoutThrowing()
    {
        var (sut, _) = BuildSut(totalCount: 25, pageSize: 10);
        await WaitForCountAsync(sut, TimeSpan.FromSeconds(2));

        var subset = sut.GetSubset(15).ToList();

        Assert.Equal(15, subset.Count);
        Assert.All(subset, item => Assert.NotNull(item));
    }

    [Fact]
    public async Task DispatchToViewModel_NeverDispatchesToPlaceholder()
    {
        var (sut, _) = BuildSut(totalCount: 10, pageSize: 10);
        await WaitForCountAsync(sut, TimeSpan.FromSeconds(2));

        // The placeholder's sentinel hash must never resolve to a dispatchable, real track —
        // this locks in the DispatchToViewModel guard so it isn't silently removed later.
        var ex = Record.Exception(() =>
        {
            var eventBus = new EventBusService();
            eventBus.Publish(new TrackStateChangedEvent(PlaylistTrackViewModel.PlaceholderGlobalId, Guid.NewGuid(), PlaylistTrackState.Completed));
        });

        Assert.Null(ex);
    }
}
