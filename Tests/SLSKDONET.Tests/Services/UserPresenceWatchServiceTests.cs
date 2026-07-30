using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class UserPresenceWatchServiceTests
{
    private static Mock<ISoulseekAdapter> CreateAdapterMock()
    {
        var mock = new Mock<ISoulseekAdapter>();
        mock.Setup(a => a.WatchUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string username, CancellationToken _) =>
                new UserWatchSnapshot(username, UserPresenceState.Online, 0, 0, 0, null, 0, null));
        mock.Setup(a => a.UnwatchUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return mock;
    }

    [Fact]
    public async Task WatchAsync_FirstCallForUsername_IssuesRealWatch()
    {
        var adapterMock = CreateAdapterMock();
        var sut = new UserPresenceWatchService(adapterMock.Object, new EventBusService(), NullLogger<UserPresenceWatchService>.Instance);

        await sut.WatchAsync("peer1");

        adapterMock.Verify(a => a.WatchUserAsync("peer1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WatchAsync_SecondCallForSameUsername_DoesNotIssueASecondWatch()
    {
        var adapterMock = CreateAdapterMock();
        var sut = new UserPresenceWatchService(adapterMock.Object, new EventBusService(), NullLogger<UserPresenceWatchService>.Instance);

        await sut.WatchAsync("peer1");
        await sut.WatchAsync("peer1");

        adapterMock.Verify(a => a.WatchUserAsync("peer1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispose_WhileAnotherHandleStillOpen_DoesNotUnwatch()
    {
        var adapterMock = CreateAdapterMock();
        var sut = new UserPresenceWatchService(adapterMock.Object, new EventBusService(), NullLogger<UserPresenceWatchService>.Instance);

        var (handle1, _) = await sut.WatchAsync("peer1");
        var (handle2, _) = await sut.WatchAsync("peer1");

        handle1.Dispose();

        adapterMock.Verify(a => a.UnwatchUserAsync("peer1", It.IsAny<CancellationToken>()), Times.Never);

        handle2.Dispose();
        // Give the fire-and-forget unwatch a moment to run.
        await Task.Delay(50);

        adapterMock.Verify(a => a.UnwatchUserAsync("peer1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WatchAsync_SecondCallForSameUsername_ReturnsCachedSnapshotWithoutRefetching()
    {
        var adapterMock = CreateAdapterMock();
        var sut = new UserPresenceWatchService(adapterMock.Object, new EventBusService(), NullLogger<UserPresenceWatchService>.Instance);

        var (_, firstSnapshot) = await sut.WatchAsync("peer1");
        var (_, secondSnapshot) = await sut.WatchAsync("peer1");

        Assert.NotNull(firstSnapshot);
        Assert.NotNull(secondSnapshot);
        Assert.Equal("peer1", secondSnapshot!.Username);
        adapterMock.Verify(a => a.WatchUserAsync("peer1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispose_CalledTwiceOnSameHandle_OnlyReleasesOnce()
    {
        var adapterMock = CreateAdapterMock();
        var sut = new UserPresenceWatchService(adapterMock.Object, new EventBusService(), NullLogger<UserPresenceWatchService>.Instance);

        var (handle, _) = await sut.WatchAsync("peer1");
        handle.Dispose();
        handle.Dispose();
        await Task.Delay(50);

        adapterMock.Verify(a => a.UnwatchUserAsync("peer1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
