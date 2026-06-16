using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Inputs;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class SafetyFilterTests
{
    private readonly Mock<ILogger<SafetyFilterService>> _loggerMock;
    private readonly AppConfig _config;
    private readonly SafetyFilterService _service;

    public SafetyFilterTests()
    {
        _loggerMock = new Mock<ILogger<SafetyFilterService>>();
        _config = new AppConfig
        {
            SearchPolicy = SearchPolicy.QualityFirst()
        };
        _service = new SafetyFilterService(_loggerMock.Object, _config);
    }

    // ── existing tests ────────────────────────────────────────────────────────

    [Fact]
    public void IsSafe_ShouldReject_BlacklistedUser()
    {
        _config.BlacklistedUsers = new List<string> { "BannedUser" };
        var track = new Track { Username = "BannedUser", Length = 300, Bitrate = 900, SampleRate = 44100, Filename = "Test Query.flac" };
        Assert.False(_service.IsSafe(track, "Test Query", 300));
    }

    [Fact]
    public void IsSafe_ShouldAllow_NormalUser()
    {
        _config.BlacklistedUsers = new List<string> { "BannedUser" };
        var track = new Track { Username = "GoodUser", Length = 300, Bitrate = 900, SampleRate = 44100, Filename = "Test Query.flac" };
        Assert.True(_service.IsSafe(track, "Test Query", 300));
    }

    [Fact]
    public void IsSafe_ShouldReject_DurationMismatch_IfStrict()
    {
        _config.SearchPolicy.DurationToleranceSeconds = 10;
        var track = new Track { Length = 500, Bitrate = 900, SampleRate = 44100, Filename = "Test Query.flac" };
        Assert.False(_service.IsSafe(track, "Test Query", 300));
    }

    [Fact]
    public void IsSafe_ShouldAllow_DurationMatch()
    {
        _config.SearchPolicy.DurationToleranceSeconds = 10;
        var track = new Track { Length = 305, Bitrate = 900, SampleRate = 44100, Filename = "Test Query.flac" };
        Assert.True(_service.IsSafe(track, "Test Query", 300));
    }

    // ── IsUpscaled ────────────────────────────────────────────────────────────

    [Fact]
    public void IsUpscaled_ReturnsFalse_WhenNoFrequencyCutoff()
    {
        var track = new PlaylistTrack { Format = "flac", Bitrate = 1000, FrequencyCutoff = null };
        Assert.False(_service.IsUpscaled(track));
    }

    [Fact]
    public void IsUpscaled_ReturnsTrue_WhenFlacCutoffBelow20kHz()
    {
        // FLAC with 16kHz cutoff is almost certainly a fake/upscaled file.
        var track = new PlaylistTrack { Format = "flac", Bitrate = 1000, FrequencyCutoff = 16000 };
        Assert.True(_service.IsUpscaled(track));
    }

    [Fact]
    public void IsUpscaled_ReturnsFalse_WhenFlacCutoffAbove20kHz()
    {
        var track = new PlaylistTrack { Format = "flac", Bitrate = 1000, FrequencyCutoff = 22000 };
        Assert.False(_service.IsUpscaled(track));
    }

    [Fact]
    public void IsUpscaled_ReturnsTrue_WhenHighBitrateButLowCutoff()
    {
        // 320kbps claiming quality but spectral cutoff at 15kHz — upscale indicator.
        var track = new PlaylistTrack { Format = "mp3", Bitrate = 320, FrequencyCutoff = 15000 };
        Assert.True(_service.IsUpscaled(track));
    }

    [Fact]
    public void IsUpscaled_ReturnsFalse_WhenHighBitrateAndHighCutoff()
    {
        // Legitimate 320kbps — cutoff above 16.1kHz threshold.
        var track = new PlaylistTrack { Format = "mp3", Bitrate = 320, FrequencyCutoff = 20000 };
        Assert.False(_service.IsUpscaled(track));
    }

    [Fact]
    public void IsUpscaled_ReturnsFalse_WhenLowBitrateAndLowCutoff()
    {
        // 128kbps with low cutoff is expected — not an upscale, just a bad file.
        var track = new PlaylistTrack { Format = "mp3", Bitrate = 128, FrequencyCutoff = 14000 };
        Assert.False(_service.IsUpscaled(track));
    }

    // ── Size heuristic ────────────────────────────────────────────────────────

    [Fact]
    public void EvaluateCandidate_Rejects_FileThatIsToSmallForBitrate()
    {
        // 900kbps FLAC lasting 300s should be ~33MB. 1MB is suspiciously small.
        var track = new Track
        {
            Filename = "track.flac",
            Bitrate = 900,
            Length = 300,
            Size = 1_000_000,  // 1 MB
            SampleRate = 44100
        };
        var result = _service.EvaluateCandidate(track, "track");
        Assert.False(result.IsSafe);
    }

    [Fact]
    public void EvaluateCandidate_Accepts_FileSizeWithinExpectedRange()
    {
        // 900kbps × 300s ÷ 8 = ~33.75 MB
        var track = new Track
        {
            Filename = "track.flac",
            Bitrate = 900,
            Length = 300,
            Size = 33_750_000,
            SampleRate = 44100
        };
        var result = _service.EvaluateCandidate(track, "track");
        Assert.True(result.IsSafe);
    }

    // ── Keyword blocklist ────────────────────────────────────────────────────

    [Theory]
    // Space-bounded so \b word boundary is respected; underscore is \w and would skip the match.
    [InlineData("artist - ringtone.flac")]
    [InlineData("track snippet.flac")]
    [InlineData("track - preview.flac")]
    [InlineData("sample pack.flac")]
    [InlineData("teaser edit.flac")]
    public void EvaluateCandidate_Rejects_BlockedKeywordsInFilename(string filename)
    {
        var track = new Track { Filename = filename, Bitrate = 900, SampleRate = 44100 };
        var result = _service.EvaluateCandidate(track, "track");
        Assert.False(result.IsSafe);
    }

    [Fact]
    public void EvaluateCandidate_Accepts_CleanFilename()
    {
        var track = new Track { Filename = "artist - title.flac", Bitrate = 900, SampleRate = 44100 };
        var result = _service.EvaluateCandidate(track, "title");
        Assert.True(result.IsSafe);
    }

    // ── Sample rate gate ─────────────────────────────────────────────────────

    [Fact]
    public void EvaluateCandidate_Rejects_SampleRateBelow44100_InStrictMode()
    {
        var track = new Track { Filename = "track.flac", Bitrate = 900, SampleRate = 22050 };
        var result = _service.EvaluateCandidate(track, "track", allowLossy: false);
        Assert.False(result.IsSafe);
    }

    [Fact]
    public void EvaluateCandidate_Accepts_44100SampleRate()
    {
        var track = new Track { Filename = "track.flac", Bitrate = 900, SampleRate = 44100 };
        var result = _service.EvaluateCandidate(track, "track", allowLossy: false);
        Assert.True(result.IsSafe);
    }

    [Fact]
    public void EvaluateCandidate_Accepts_HiResSampleRate()
    {
        var track = new Track { Filename = "track.flac", Bitrate = 1400, SampleRate = 96000 };
        var result = _service.EvaluateCandidate(track, "track", allowLossy: false);
        Assert.True(result.IsSafe);
    }

    // ── Extension guards ────────────────────────────────────────────────────

    [Fact]
    public void EvaluateCandidate_Rejects_BannedExtension()
    {
        var track = new Track { Filename = "malware.exe", Bitrate = 900, SampleRate = 44100 };
        var result = _service.EvaluateCandidate(track, "track");
        Assert.False(result.IsSafe);
    }

    [Fact]
    public void EvaluateCandidate_Rejects_LossyExtension_InStrictMode()
    {
        var track = new Track { Filename = "song.mp3", Bitrate = 320, SampleRate = 44100 };
        var result = _service.EvaluateCandidate(track, "song", allowLossy: false);
        Assert.False(result.IsSafe);
    }

    [Fact]
    public void EvaluateCandidate_Accepts_LossyExtension_WhenAllowLossyIsTrue()
    {
        var track = new Track { Filename = "song.mp3", Bitrate = 320, SampleRate = 44100 };
        var result = _service.EvaluateCandidate(track, "song", allowLossy: true);
        Assert.True(result.IsSafe);
    }
}
