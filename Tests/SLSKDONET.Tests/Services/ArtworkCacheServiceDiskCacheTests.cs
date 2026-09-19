using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Remote artwork previously had zero disk persistence: every app restart (and every time a
/// WeakReference-cached bitmap was garbage collected mid-session) silently re-downloaded every
/// album's art from the network. These tests pin the fixed behavior on <see cref="ArtworkCacheService.ResolveBytesAsync"/>
/// — the byte-resolution step, tested separately from Bitmap decoding since decoding needs a live
/// Avalonia/Skia platform that isn't available in a plain unit test host.
/// </summary>
public sealed class ArtworkCacheServiceDiskCacheTests : IDisposable
{
    private readonly string _tempDiskCacheDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ArtworkCacheTest_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDiskCacheDir))
            Directory.Delete(_tempDiskCacheDir, recursive: true);
    }

    private static readonly byte[] FakeImageBytes = { 1, 2, 3, 4, 5 };

    [Fact]
    public async Task ResolveBytesAsync_RemoteUrl_PersistsToDiskCache()
    {
        var handler = new CountingHandler(FakeImageBytes);
        var service = new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient(handler), _tempDiskCacheDir);

        var bytes = await service.ResolveBytesAsync("http://example.invalid/art.jpg");

        Assert.Equal(FakeImageBytes, bytes);
        Assert.Equal(1, handler.RequestCount);
        Assert.True(Directory.Exists(_tempDiskCacheDir));
        Assert.Single(Directory.GetFiles(_tempDiskCacheDir));
    }

    [Fact]
    public async Task ResolveBytesAsync_SecondServiceInstance_ReadsFromDiskWithoutNetworkCall()
    {
        // First "session": downloads and persists to disk.
        var firstHandler = new CountingHandler(FakeImageBytes);
        var firstService = new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient(firstHandler), _tempDiskCacheDir);
        await firstService.ResolveBytesAsync("http://example.invalid/art.jpg");
        Assert.Equal(1, firstHandler.RequestCount);

        // Second "session" (fresh in-memory state, same disk cache dir, network throws if hit):
        // simulates an app restart, where the disk cache from the prior session should still be there.
        var secondHandler = new ThrowingHandler();
        var secondService = new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient(secondHandler), _tempDiskCacheDir);

        var bytes = await secondService.ResolveBytesAsync("http://example.invalid/art.jpg");

        Assert.Equal(FakeImageBytes, bytes);
        Assert.Equal(0, secondHandler.RequestCount);
    }

    [Fact]
    public async Task ResolveBytesAsync_DifferentUrls_GetDistinctDiskCacheFiles()
    {
        var handler = new CountingHandler(FakeImageBytes);
        var service = new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient(handler), _tempDiskCacheDir);

        await service.ResolveBytesAsync("http://example.invalid/album-a.jpg");
        await service.ResolveBytesAsync("http://example.invalid/album-b.jpg");

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, Directory.GetFiles(_tempDiskCacheDir).Length);
    }

    [Fact]
    public async Task ResolveBytesAsync_LocalFilePath_DoesNotTouchNetwork()
    {
        var localFile = Path.Combine(Path.GetTempPath(), $"ORBIT_ArtworkCacheTest_LocalFile_{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(localFile, FakeImageBytes);
        try
        {
            var handler = new ThrowingHandler();
            var service = new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient(handler), _tempDiskCacheDir);

            var bytes = await service.ResolveBytesAsync(localFile);

            Assert.Equal(FakeImageBytes, bytes);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            File.Delete(localFile);
        }
    }

    [Fact]
    public async Task ResolveBytesAsync_NonexistentLocalPath_ReturnsNull()
    {
        var service = new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient(new ThrowingHandler()), _tempDiskCacheDir);

        var bytes = await service.ResolveBytesAsync(Path.Combine(_tempDiskCacheDir, "does-not-exist.jpg"));

        Assert.Null(bytes);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        public int RequestCount;

        public CountingHandler(byte[] body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            throw new HttpRequestException("Network should not be hit — expected to be served from disk cache.");
        }
    }
}
