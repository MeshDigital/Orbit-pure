using SLSKDONET.Services.AutoDownload;
using Xunit;

namespace SLSKDONET.Tests.AutoDownload;

public class CandidateSizePlausibilityTests
{
    private static CandidateSizePlausibility.Result Eval(
        long? size, int bitrate, double? length, double? expected, string format) =>
        CandidateSizePlausibility.Evaluate(size, bitrate, length, expected, format);

    [Fact]
    public void ThirtySecondMp3Preview_IsRejected()
    {
        // Peer reports the full length (300 s) while the file is only a 30 s preview.
        var r = Eval(1_200_000, 320, 300, 300, "mp3");
        Assert.False(r.IsPlausible);
        Assert.Contains("implied", r.Reason);
    }

    [Fact]
    public void FullMp3_IsAccepted()
    {
        Assert.True(Eval(12_000_000, 320, 300, 300, "mp3").IsPlausible);
    }

    [Fact]
    public void TruncatedFlac_IsRejected()
    {
        Assert.False(Eval(3_000_000, 0, 360, 360, "flac").IsPlausible);
    }

    [Fact]
    public void NormalFlac_IsAccepted()
    {
        Assert.True(Eval(40_000_000, 0, 360, 360, "flac").IsPlausible);
    }

    [Fact]
    public void VbrV0Mp3_IsAccepted()
    {
        Assert.True(Eval(8_500_000, 0, 300, 300, "mp3").IsPlausible);
    }

    [Fact]
    public void UnknownDuration_OnlyFloorApplies()
    {
        Assert.True(Eval(600_000, 0, null, null, "mp3").IsPlausible);
    }

    [Fact]
    public void NullSize_IsNotRejected()
    {
        Assert.True(Eval(null, 320, 30, 300, "mp3").IsPlausible);
    }

    [Fact]
    public void PeerClaimsFullLengthButFileIsTruncated_IsRejected()
    {
        Assert.False(Eval(3_000_000, 0, 360, 360, "flac").IsPlausible);
    }

    [Fact]
    public void LosslessFloor_IsConfigurable()
    {
        var r = CandidateSizePlausibility.Evaluate(3_000_000, 0, 360, 360, "flac", losslessMinImpliedKbps: 50);
        Assert.True(r.IsPlausible);
    }
}