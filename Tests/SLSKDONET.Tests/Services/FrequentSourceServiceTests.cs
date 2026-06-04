using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class FrequentSourceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public FrequentSourceServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var setup = new AppDbContext(_options);
        setup.Database.EnsureCreated();
    }

    [Fact]
    public async Task UpsertAsync_SameSourceAndFolder_IncrementsCountAndBytes()
    {
        var sut = CreateSut(enabled: true);

        var timestampA = new DateTime(2026, 5, 28, 9, 0, 0, DateTimeKind.Utc);
        var timestampB = timestampA.AddMinutes(2);

        await sut.UpsertAsync("peerA", @"\\music\\house", 1000, timestampA);
        await sut.UpsertAsync("peerA", @"\\music\\house", 500, timestampB);

        var ranked = await sut.GetRankedAsync();
        var entry = Assert.Single(ranked);
        Assert.Equal(2, entry.DownloadCount);
        Assert.Equal(1500, entry.TotalBytesDownloaded);
        Assert.Equal(timestampB, entry.LastDownloadedAtUtc);
    }

    [Fact]
    public async Task GetRankedAsync_PrioritizesPinnedThenFriendThenCount()
    {
        var sut = CreateSut(enabled: true);

        await sut.UpsertAsync("alpha", @"\\x", 200, DateTime.UtcNow.AddMinutes(-10));
        await sut.UpsertAsync("beta", @"\\y", 200, DateTime.UtcNow.AddMinutes(-9));
        await sut.UpsertAsync("gamma", @"\\z", 200, DateTime.UtcNow.AddMinutes(-8));

        await sut.SetFriendAsync("beta", @"\\y", true);
        await sut.PinAsync("gamma", @"\\z", true);

        var ranked = await sut.GetRankedAsync(limit: 3);
        Assert.Equal(3, ranked.Count);
        Assert.Equal("gamma", ranked[0].SourceUsername); // pinned wins
        Assert.Equal("beta", ranked[1].SourceUsername);  // then friend
        Assert.Equal("alpha", ranked[2].SourceUsername);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllRows()
    {
        var sut = CreateSut(enabled: true);

        await sut.UpsertAsync("peerA", @"\\folderA", 100, DateTime.UtcNow);
        await sut.UpsertAsync("peerB", @"\\folderB", 200, DateTime.UtcNow);

        await sut.ClearAsync();

        var ranked = await sut.GetRankedAsync();
        Assert.Empty(ranked);
    }

    [Fact]
    public async Task DisabledFeature_ReturnsEmptyAndSkipsWrites()
    {
        var sut = CreateSut(enabled: false);

        await sut.UpsertAsync("peerA", @"\\folderA", 100, DateTime.UtcNow);
        var ranked = await sut.GetRankedAsync();

        Assert.Empty(ranked);
    }

    [Fact]
    public async Task UpsertAsync_NormalizesUsernameAndFolderPath()
    {
        var sut = CreateSut(enabled: true);

        await sut.UpsertAsync(" PeerA ", @"\\Music\\House\\", 100, DateTime.UtcNow.AddMinutes(-1));
        await sut.UpsertAsync("peera", @"//music//house", 200, DateTime.UtcNow);

        var ranked = await sut.GetRankedAsync();
        var entry = Assert.Single(ranked);

        Assert.Equal("peera", entry.SourceUsername);
        Assert.Equal("\\music\\house", entry.FolderPath);
        Assert.Equal(2, entry.DownloadCount);
        Assert.Equal(300, entry.TotalBytesDownloaded);
    }

    [Fact]
    public async Task UpsertAsync_OlderTimestamp_DoesNotRegressLastDownloadedAtUtc()
    {
        var sut = CreateSut(enabled: true);
        var newer = new DateTime(2026, 5, 28, 11, 0, 0, DateTimeKind.Utc);
        var older = newer.AddMinutes(-15);

        await sut.UpsertAsync("peerA", @"\\folderA", 100, newer);
        await sut.UpsertAsync("peerA", @"\\folderA", 200, older);

        var ranked = await sut.GetRankedAsync();
        var entry = Assert.Single(ranked);
        Assert.Equal(newer, entry.LastDownloadedAtUtc);
        Assert.Equal(300, entry.TotalBytesDownloaded);
        Assert.Equal(2, entry.DownloadCount);
    }

    [Fact]
    public async Task UpdateFlags_CreateMissingRowAndPersistValues()
    {
        var sut = CreateSut(enabled: true);

        await sut.PinAsync("PeerX", @"\\FolderX\\", true);
        await sut.SetFriendAsync(" peerx ", @"//folderx", true);
        await sut.AddNoteAsync("peerx", @"\\folderx", "trusted source");

        var ranked = await sut.GetRankedAsync();
        var entry = Assert.Single(ranked);

        Assert.Equal("peerx", entry.SourceUsername);
        Assert.Equal("\\folderx", entry.FolderPath);
        Assert.True(entry.IsPinned);
        Assert.True(entry.IsFriend);
        Assert.Equal("trusted source", entry.LocalNote);
        Assert.Equal(0, entry.DownloadCount);
    }

    [Fact]
    public async Task UpdateFlags_DisabledFeature_DoesNotCreateRows()
    {
        var sut = CreateSut(enabled: false);

        await sut.PinAsync("peerA", @"\\folderA", true);
        await sut.SetFriendAsync("peerA", @"\\folderA", true);
        await sut.AddNoteAsync("peerA", @"\\folderA", "should-not-write");

        var ranked = await sut.GetRankedAsync();
        Assert.Empty(ranked);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private FrequentSourceService CreateSut(bool enabled)
    {
        var dbFactoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        dbFactoryMock
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
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
}