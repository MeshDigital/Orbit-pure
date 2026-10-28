using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Platform;
using Xunit;

namespace SLSKDONET.Tests.Services.Platform;

public sealed class FfmpegInstallerServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orbit-ffmpeg-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task TamperedDownload_IsRejectedAndNothingIsInstalled()
    {
        if (!FfmpegInstallerService.CanAutoInstall) return; // Windows x64 only

        var installer = new FfmpegInstallerService(NullLogger<FfmpegInstallerService>.Instance)
        {
            TargetDirectory = _dir,
            Http = new HttpClient(new StubHandler(new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 })),
        };

        var result = await installer.InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("corrupted or changed", result.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "ffmpeg.exe")));
        Assert.Empty(Directory.GetFiles(_dir)); // the partial download is cleaned up too
    }

    [Fact]
    public async Task ExistingInstall_IsNotDownloadedAgain()
    {
        if (!FfmpegInstallerService.CanAutoInstall) return;

        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ffmpeg.exe"), "stub");
        var installer = new FfmpegInstallerService(NullLogger<FfmpegInstallerService>.Instance)
        {
            TargetDirectory = _dir,
            Http = new HttpClient(new StubHandler(Array.Empty<byte>())),
        };

        var result = await installer.InstallAsync();

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_dir, "ffmpeg.exe"), result.Path);
    }
}
