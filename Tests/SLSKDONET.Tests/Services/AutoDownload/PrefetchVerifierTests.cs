using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services.AutoDownload;
using Xunit;

namespace SLSKDONET.Tests.Services.AutoDownload;

public class PrefetchVerifierTests
{
    [Fact]
    public async Task VerifyDownloadAsync_UsesPerTrackMimeAliasPolicy_WhenConfigDisallowsRawExt()
    {
        var config = new AppConfig
        {
            EnableAutoDownloadStrictMode = true,
            AutoDownloadDiagnosticsEnabled = false,
            AutoDownloadMinFileSizeBytes = 16,
            AutoDownloadAllowedExtensions = new() { "flac" }
        };

        var verifier = new PrefetchVerifier(
            NullLogger<PrefetchVerifier>.Instance,
            config,
            null!,
            null!);

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Massive Attack",
            Title = "Teardrop",
            PreferredFormats = "audio/mpeg; charset=utf-8"
        };

        var candidate = new Track
        {
            Filename = "Massive Attack - Teardrop.mp3",
            Format = "audio/mpeg; charset=utf-8",
            Bitrate = 320,
            Size = 8_000_000,
            Length = 330
        };

        var filePath = Path.Combine(Path.GetTempPath(), $"orbit-prefetch-{Guid.NewGuid():N}.mp3");

        try
        {
            await File.WriteAllBytesAsync(filePath, new byte[128]);

            var result = await verifier.VerifyDownloadAsync(track, candidate, filePath);

            Assert.Equal(VerificationResult.Success, result);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task VerifyDownloadAsync_FallsBackToCandidateFormat_WhenStagedPathHasNoExtension()
    {
        var config = new AppConfig
        {
            EnableAutoDownloadStrictMode = true,
            AutoDownloadDiagnosticsEnabled = false,
            AutoDownloadMinFileSizeBytes = 16,
            AutoDownloadAllowedExtensions = new() { "flac" }
        };

        var verifier = new PrefetchVerifier(
            NullLogger<PrefetchVerifier>.Instance,
            config,
            null!,
            null!);

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Burial",
            Title = "Archangel",
            PreferredFormats = "mp3"
        };

        var candidate = new Track
        {
            Filename = "Burial - Archangel",
            Format = "audio/mpeg; charset=utf-8",
            Bitrate = 320,
            Size = 8_000_000,
            Length = 246
        };

        var filePath = Path.Combine(Path.GetTempPath(), $"orbit-prefetch-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllBytesAsync(filePath, new byte[128]);

            var result = await verifier.VerifyDownloadAsync(track, candidate, filePath);

            Assert.Equal(VerificationResult.Success, result);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
