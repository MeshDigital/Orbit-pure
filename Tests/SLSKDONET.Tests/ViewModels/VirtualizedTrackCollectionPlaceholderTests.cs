using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
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
/// "Loading…" rows must resolve: when the count promises rows the page query can't return (a
/// failing query on a fresh install, or rows deleted between the two queries), the collection
/// shrinks to what loaded and, for a failing query, flags an error state instead of leaving
/// placeholders up forever.
/// </summary>
public class VirtualizedTrackCollectionPlaceholderTests
{
    private static VirtualizedTrackCollection Build(Func<int> count, Func<int, int, int> rowsAvailable, Mock<ILibraryService>? mock = null)
    {
        var playlistId = Guid.NewGuid();
        mock ??= new Mock<ILibraryService>();
        mock.Setup(s => s.GetTrackCountAsync(playlistId, null, null, null, null, null)).ReturnsAsync(() => count());
        mock.Setup(s => s.GetPagedPlaylistTracksAsync(playlistId, It.IsAny<int>(), It.IsAny<int>(), null, null, null, null, TrackSortColumn.Default, false, null))
            .ReturnsAsync((Guid _, int skip, int take, string? _, bool? _, IEnumerable<string>? _, string? _, TrackSortColumn _, bool _, string? _) =>
                Enumerable.Range(skip, Math.Max(0, rowsAvailable(skip, take)))
                    .Select(i => new PlaylistTrack { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = $"h{i}", Artist = "A", Title = $"T{i}" })
                    .ToList());

        return new VirtualizedTrackCollection(
            NullLogger<VirtualizedTrackCollection>.Instance, mock.Object, new EventBusService(),
            new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient()), playlistId, pageSize: 10);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
    }

    [Fact]
    public async Task FailingPageQuery_ShrinksToNothing_AndFlagsAnError()
    {
        var sut = Build(count: () => 25, rowsAvailable: (_, _) => 0); // the count works, every page comes back empty
        await WaitUntil(() => sut.Count != -1);
        Assert.Equal(25, sut.Count);

        Assert.True(sut[0].IsPlaceholder);
        await sut.LoadPageAsync(0);

        Assert.Equal(0, sut.Count);
        Assert.True(sut.HasLoadError);
        Assert.False(string.IsNullOrEmpty(sut.LoadErrorMessage));
    }

    [Fact]
    public async Task ThrowingPageQuery_IsHandledTheSameWay()
    {
        var mock = new Mock<ILibraryService>();
        var sut = Build(count: () => 25, rowsAvailable: (_, _) => throw new InvalidOperationException("no such column: a.VectorEmbedding"), mock);
        await WaitUntil(() => sut.Count != -1);

        await sut.LoadPageAsync(0);

        Assert.Equal(0, sut.Count);
        Assert.True(sut.HasLoadError);
    }

    [Fact]
    public async Task RowsDeletedBetweenCountAndPage_ShrinksWithoutAnError()
    {
        var live = 25;
        var sut = Build(count: () => live, rowsAvailable: (skip, take) => Math.Min(take, live - skip));
        await WaitUntil(() => sut.Count != -1);
        Assert.Equal(25, sut.Count);

        live = 14; // 11 rows removed after the count was taken
        await sut.LoadPageAsync(1);

        Assert.Equal(14, sut.Count);
        Assert.False(sut.HasLoadError);
        Assert.True(sut.TryGetLoaded(13, out var last));
        Assert.Equal("T13", last!.Model.Title);
    }

    [Fact]
    public async Task ZeroCount_IsAnEmptyList_NotAnError()
    {
        var sut = Build(count: () => 0, rowsAvailable: (_, _) => 0);
        await WaitUntil(() => sut.Count != -1);

        Assert.Equal(0, sut.Count);
        Assert.False(sut.HasLoadError);
    }
}
