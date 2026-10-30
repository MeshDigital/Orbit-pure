using System.Linq;
using SLSKDONET.Services.Copilot;
using Xunit;

namespace SLSKDONET.Tests.Services.Copilot;

public class ClashDetectorTests
{
    [Theory]
    [InlineData("8A", 8)]
    [InlineData("12B", 12)]
    [InlineData("1A", 1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    [InlineData("garbage", -1)]
    [InlineData("13A", -1)]
    public void CamelotNumberFromKey_Parses(string? key, int expected)
        => Assert.Equal(expected, ClashDetector.CamelotNumberFromKey(key));

    [Theory]
    [InlineData(1, 12, 1)] // wraps around the wheel
    [InlineData(8, 3, 5)]
    [InlineData(8, 8, 0)]
    [InlineData(8, -1, int.MaxValue)] // unknown key → no distance
    public void CamelotDistance_Computes(int a, int b, int expected)
        => Assert.Equal(expected, ClashDetector.CamelotDistance(a, b));

    [Fact]
    public void DetectClashes_FlagsVocalCollisionWithinMixWindow()
    {
        // 120 BPM → 2s/bar; 8 bars → 16s window. A mix-out at 100s → A window [84,100].
        var spine = new[]
        {
            new TrackClashData("A", MixOutSeconds: 100, MixInSeconds: 0, BarsBeforeMix: 8, CamelotNumber: 8,
                new[] { new VocalSegment(90, 110) }), // overlaps [84,100]
            new TrackClashData("B", MixOutSeconds: 0, MixInSeconds: 0, BarsBeforeMix: 8, CamelotNumber: 8,
                new[] { new VocalSegment(0, 20) }), // overlaps [0,16]
        };

        var reports = ClashDetector.DetectClashes(spine, bpm: 120);

        Assert.Contains(reports, r => r.Reason == ClashDetector.VocalOverlapReason);
    }

    [Fact]
    public void DetectClashes_IgnoresVocalOutsideMixWindow()
    {
        var spine = new[]
        {
            new TrackClashData("A", MixOutSeconds: 100, MixInSeconds: 0, BarsBeforeMix: 8, CamelotNumber: 8,
                new[] { new VocalSegment(0, 50) }), // ends well before the [84,100] mix window
            new TrackClashData("B", MixOutSeconds: 0, MixInSeconds: 0, BarsBeforeMix: 8, CamelotNumber: 8,
                new[] { new VocalSegment(0, 20) }),
        };

        var reports = ClashDetector.DetectClashes(spine, bpm: 120);

        Assert.DoesNotContain(reports, r => r.Reason == ClashDetector.VocalOverlapReason);
    }

    [Fact]
    public void DetectClashes_FlagsHarmonicDissonance()
    {
        var spine = new[]
        {
            new TrackClashData("A", MixOutSeconds: 100, MixInSeconds: 0, BarsBeforeMix: 8, CamelotNumber: 8, System.Array.Empty<VocalSegment>()),
            new TrackClashData("B", MixOutSeconds: 0, MixInSeconds: 0, BarsBeforeMix: 8, CamelotNumber: 3, System.Array.Empty<VocalSegment>()),
        };

        var reports = ClashDetector.DetectClashes(spine, bpm: 120, maxCamelotDistance: 1);

        Assert.Contains(reports, r => r.Reason == ClashDetector.HarmonicDissonanceReason);
    }
}
