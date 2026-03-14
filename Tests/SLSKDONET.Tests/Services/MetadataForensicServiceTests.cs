using SLSKDONET.Models;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class MetadataForensicServiceTests
{
    [Fact]
    public void IsSuspiciousLossless_ShouldFlag_FlacWithLossyBitrate()
    {
        var track = new Track
        {
            Filename = "Artist - Track.flac",
            Format = "flac",
            Bitrate = 320,
            SampleRate = 44100,
            BitDepth = 16
        };

        Assert.True(MetadataForensicService.IsSuspiciousLossless(track));
        Assert.Contains("320", MetadataForensicService.GetSuspiciousLosslessReason(track));
    }

    [Fact]
    public void IsSuspiciousLossless_ShouldNotFlag_HighBitrateFlac()
    {
        var track = new Track
        {
            Filename = "Artist - Track.flac",
            Format = "flac",
            Bitrate = 948,
            SampleRate = 44100,
            BitDepth = 16
        };

        Assert.False(MetadataForensicService.IsSuspiciousLossless(track));
    }

    [Fact]
    public void CalculateTier_ShouldDowngrade_SuspiciousFlacToGarbage()
    {
        var track = new Track
        {
            Filename = "Artist - Track.flac",
            Format = "flac",
            Bitrate = 192,
            SampleRate = 44100,
            BitDepth = 16,
            HasFreeUploadSlot = true,
            UploadSpeed = 1024
        };

        Assert.Equal(SearchTier.Garbage, MetadataForensicService.CalculateTier(track));
        Assert.True(MetadataForensicService.IsFake(track));
    }
}