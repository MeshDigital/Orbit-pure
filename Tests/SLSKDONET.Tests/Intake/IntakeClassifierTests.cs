using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Intake;
using Xunit;

namespace SLSKDONET.Tests.Intake;

public sealed class IntakeClassifierTests
{
    private static IntakeFacts Facts(
        string ext = ".flac", bool readable = true, string? artist = "Artist", string? title = "Title",
        int duration = 300, int bitrate = 900, int decodeErrors = 0,
        AudioAuthenticityVerdict? spectral = AudioAuthenticityVerdict.GenuineLossless, double confidence = 0.9,
        double? cutoff = 22000) =>
        new(ext, readable, artist, title, duration, bitrate, decodeErrors, spectral, confidence, cutoff);

    private static IntakeLibraryMatch Owned(string format, int bitrate, IntakeMatchKind kind = IntakeMatchKind.Metadata) =>
        new("hash", @"D:\Music\owned" + "." + format, format, bitrate, kind);

    [Fact]
    public void GenuineLosslessNotOwned_IsGoodAndKept()
    {
        var v = IntakeClassifier.Classify(Facts(), null);
        Assert.Equal(IntakeLane.Good, v.Lane);
        Assert.Equal(IntakeAction.Keep, v.Action);
        Assert.Contains("Lossless FLAC", v.Reason);
    }

    [Fact]
    public void Unreadable_IsRejected()
    {
        var v = IntakeClassifier.Classify(Facts(readable: false), null);
        Assert.Equal(IntakeLane.Reject, v.Lane);
        Assert.Equal(IntakeAction.Skip, v.Action);
    }

    [Theory]
    [InlineData(4, IntakeLane.Good)]
    [InlineData(5, IntakeLane.Reject)]
    public void DecodeErrors_RejectOnlyFromTheThreshold(int errors, IntakeLane expected) =>
        Assert.Equal(expected, IntakeClassifier.Classify(Facts(decodeErrors: errors), null).Lane);

    [Theory]
    [InlineData(30, IntakeLane.Reject)]
    [InlineData(44, IntakeLane.Reject)]
    [InlineData(45, IntakeLane.Good)]
    [InlineData(0, IntakeLane.Good)] // unknown duration is not a preview
    public void ShortFiles_AreRejectedAsPreviews(int seconds, IntakeLane expected) =>
        Assert.Equal(expected, IntakeClassifier.Classify(Facts(duration: seconds), null).Lane);

    [Fact]
    public void ExactCopyOfOwnedAudio_IsDuplicate_EvenWhenLabelledBetter()
    {
        var v = IntakeClassifier.Classify(Facts(), Owned("mp3", 128, IntakeMatchKind.ContentHash));
        Assert.Equal(IntakeLane.Duplicate, v.Lane);
        Assert.Equal(IntakeAction.Skip, v.Action);
    }

    [Fact]
    public void LosslessCopyOfOwnedMp3_IsBetterCopyReplacingIt()
    {
        var v = IntakeClassifier.Classify(Facts(), Owned("mp3", 192));
        Assert.Equal(IntakeLane.BetterCopy, v.Lane);
        Assert.Equal(IntakeAction.Replace, v.Action);
        Assert.Contains("192 kbps MP3", v.Reason);
    }

    [Fact]
    public void SameQualityAsOwned_IsDuplicate()
    {
        var v = IntakeClassifier.Classify(Facts(), Owned("flac", 900));
        Assert.Equal(IntakeLane.Duplicate, v.Lane);
    }

    [Fact]
    public void FakeLossless_IsSuspicious_AndNeverBeatsAnOwnedCopy()
    {
        var fake = Facts(spectral: AudioAuthenticityVerdict.TranscodedMediumBitrate, cutoff: 16000);
        var alone = IntakeClassifier.Classify(fake, null);
        Assert.Equal(IntakeLane.Suspicious, alone.Lane);
        Assert.Equal(IntakeAction.Upgrade, alone.Action);
        Assert.Contains("16 kHz", alone.Reason);

        var owned = IntakeClassifier.Classify(fake, Owned("mp3", 192));
        Assert.Equal(IntakeLane.Duplicate, owned.Lane);
    }

    [Fact]
    public void LowConfidenceSpectralFlag_IsIgnored()
    {
        var v = IntakeClassifier.Classify(Facts(spectral: AudioAuthenticityVerdict.TranscodedLowBitrate, confidence: 0.4), null);
        Assert.Equal(IntakeLane.Good, v.Lane);
    }

    [Theory]
    [InlineData(128, IntakeLane.Suspicious)]
    [InlineData(255, IntakeLane.Suspicious)]
    [InlineData(256, IntakeLane.Good)]
    [InlineData(320, IntakeLane.Good)]
    public void LossyBitrate_BelowThresholdIsSuspicious(int kbps, IntakeLane expected) =>
        Assert.Equal(expected, IntakeClassifier.Classify(Facts(ext: ".mp3", bitrate: kbps, spectral: null), null).Lane);

    [Fact]
    public void Upscaled320_IsSuspicious()
    {
        var v = IntakeClassifier.Classify(Facts(ext: ".mp3", bitrate: 320, spectral: AudioAuthenticityVerdict.TranscodedLowBitrate), null);
        Assert.Equal(IntakeLane.Suspicious, v.Lane);
        Assert.Contains("320 kbps", v.Reason);
    }

    [Fact]
    public void MissingTags_StaysGood_ButSaysSo()
    {
        var v = IntakeClassifier.Classify(Facts(artist: null), null);
        Assert.Equal(IntakeLane.Good, v.Lane);
        Assert.Contains("missing artist or title", v.Reason);
    }
}
