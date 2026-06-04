using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Tests.Helpers;
using Xunit;

namespace SLSKDONET.Tests.Services;

public sealed class SoulseekAdapterFrequentSourceHookTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SoulseekAdapterFrequentSourceHookTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var setup = new AppDbContext(_options);
        setup.Database.EnsureCreated();
    }

    [ProfileTest("frequent-sources")]
    public void TryExtractRemoteFolderPath_NormalizesAndExtractsExpectedFolder()
    {
        var cases = new (string? RemoteFilename, string? Expected)[]
        {
            (null, null),
            (string.Empty, null),
            ("track.flac", null),
            ("Artist\\Album\\track.flac", "Artist\\Album"),
            ("Artist/Album/track.flac", "Artist\\Album"),
            ("  Artist\\Album\\track.flac  ", "Artist\\Album")
        };

        foreach (var testCase in cases)
        {
            var actual = SoulseekAdapter.TryExtractRemoteFolderPath(testCase.RemoteFilename);
            Assert.Equal(testCase.Expected, actual);
        }
    }

    [ProfileTest("frequent-sources")]
    public async Task OnTransferCompletedForFrequentSourcesAsync_UpsertsFrequentSource()
    {
        var frequentSourceService = CreateSut(enabled: true);
        var timestampUtc = new DateTime(2026, 5, 28, 10, 0, 0, DateTimeKind.Utc);

        await InvokeHookAsync(
            frequentSourceService,
            "peer-a",
            "Artist\\Album",
            4096L,
            timestampUtc);

        var ranked = await frequentSourceService.GetRankedAsync();
        var entry = Assert.Single(ranked);
        Assert.Equal("peer-a", entry.SourceUsername);
        Assert.Equal("artist\\album", entry.FolderPath);
        Assert.Equal(1, entry.DownloadCount);
        Assert.Equal(4096L, entry.TotalBytesDownloaded);
        Assert.Equal(timestampUtc, entry.LastDownloadedAtUtc);
    }

    [ProfileTest("frequent-sources")]
    public async Task OnTransferCompletedForFrequentSourcesAsync_NullService_NoThrow()
    {
        var timestampUtc = new DateTime(2026, 5, 28, 10, 5, 0, DateTimeKind.Utc);

        await InvokeHookAsync(
            null,
            "peer-a",
            "Artist\\Album",
            2048L,
            timestampUtc);
    }

    [ProfileTest("frequent-sources")]
    public async Task OnTransferCompletedForFrequentSourcesAsync_DisabledFeature_DoesNotWrite()
    {
        var frequentSourceService = CreateSut(enabled: false);
        var timestampUtc = new DateTime(2026, 5, 28, 10, 10, 0, DateTimeKind.Utc);

        await InvokeHookAsync(
            frequentSourceService,
            "peer-b",
            "Artist\\EP",
            1024L,
            timestampUtc);

        var ranked = await frequentSourceService.GetRankedAsync();
        Assert.Empty(ranked);
    }

    [ProfileTest("frequent-sources")]
    public async Task TryRecordFrequentSourceDownloadAsync_UsesObservedBytesWhenExpectedSizeMissing()
    {
        var frequentSourceService = CreateSut(enabled: true);
        var timestampUtc = new DateTime(2026, 5, 28, 10, 15, 0, DateTimeKind.Utc);

        await SoulseekAdapter.TryRecordFrequentSourceDownloadAsync(
            frequentSourceService,
            "Peer-C",
            "Artist/Album/track.flac",
            expectedSize: null,
            observedBytes: 321,
            completedAtUtc: timestampUtc,
            cancellationToken: CancellationToken.None);

        var ranked = await frequentSourceService.GetRankedAsync();
        var entry = Assert.Single(ranked);
        Assert.Equal("peer-c", entry.SourceUsername);
        Assert.Equal("artist\\album", entry.FolderPath);
        Assert.Equal(321L, entry.TotalBytesDownloaded);
    }

    [ProfileTest("frequent-sources")]
    public async Task TryRecordFrequentSourceDownloadAsync_InvalidRemoteFilename_DoesNotWrite()
    {
        var frequentSourceService = CreateSut(enabled: true);

        await SoulseekAdapter.TryRecordFrequentSourceDownloadAsync(
            frequentSourceService,
            "peer-d",
            "track-only.flac",
            expectedSize: 900,
            observedBytes: 900,
            completedAtUtc: DateTime.UtcNow,
            cancellationToken: CancellationToken.None);

        var ranked = await frequentSourceService.GetRankedAsync();
        Assert.Empty(ranked);
    }

    [ProfileTest("frequent-sources")]
    public async Task TryRecordFrequentSourceDownloadAsync_CanceledToken_PropagatesCancellation()
    {
        var dbFactoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        dbFactoryMock
            .Setup(factory => factory.CreateDbContextAsync(It.Is<CancellationToken>(ct => ct.IsCancellationRequested)))
            .ThrowsAsync(new OperationCanceledException());
        dbFactoryMock
            .Setup(factory => factory.CreateDbContextAsync(It.Is<CancellationToken>(ct => !ct.IsCancellationRequested)))
            .ReturnsAsync(() => new AppDbContext(_options));

        var frequentSourceService = new FrequentSourceService(
            dbFactoryMock.Object,
            null,
            new AppConfig { EnableFrequentSources = true },
            NullLogger<FrequentSourceService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SoulseekAdapter.TryRecordFrequentSourceDownloadAsync(
                frequentSourceService,
                "peer-e",
                "Artist/Album/track.flac",
                expectedSize: 100,
                observedBytes: 100,
                completedAtUtc: DateTime.UtcNow,
                cancellationToken: cts.Token));
    }

    private static async Task InvokeHookAsync(
        FrequentSourceService? frequentSourceService,
        string username,
        string folderPath,
        long bytesDownloaded,
        DateTime completedAtUtc)
    {
        await SoulseekAdapter.OnTransferCompletedForFrequentSourcesAsync(
            frequentSourceService,
            username,
            folderPath,
            bytesDownloaded,
            completedAtUtc,
            CancellationToken.None);
    }

    private FrequentSourceService CreateSut(bool enabled)
    {
        var dbFactoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        dbFactoryMock
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));

        var config = new AppConfig
        {
            EnableFrequentSources = enabled
        };

        return new FrequentSourceService(
            dbFactoryMock.Object,
            null,
            config,
            NullLogger<FrequentSourceService>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
