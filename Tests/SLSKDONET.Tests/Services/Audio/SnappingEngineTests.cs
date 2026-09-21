using System.Linq;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>
/// Locks in <see cref="SnappingEngine"/>'s behavior after its grid-candidate generation was
/// changed to delegate beat/second conversion to <see cref="SLSKDONET.Services.Timeline.BeatGridService"/>
/// instead of re-deriving `60/bpm` locally (part of the snap-to-grid consolidation pass — see
/// BeatGridService's own tests for the shared conversion math).
/// </summary>
public class SnappingEngineTests
{
    [Fact]
    public void GetSnapCandidates_IncludesBeatBarAndPhraseLines_At120Bpm()
    {
        // 120 BPM: beat=0.5s, bar(4 beats)=2.0s, phrase(64 beats)=32.0s.
        var candidates = SnappingEngine.GetSnapCandidates(120f, System.Array.Empty<float>(), 0f, 2.5f);

        Assert.Contains(0.5f, candidates);
        Assert.Contains(1.0f, candidates);
        Assert.Contains(2.0f, candidates); // bar line
        Assert.Contains(0f, candidates);   // phrase line at track start
    }

    [Fact]
    public void GetSnapCandidates_IncludesLandmarksWithinWindow_ExcludesOutside()
    {
        var landmarks = new[] { 1.23f, 50f };
        var candidates = SnappingEngine.GetSnapCandidates(120f, landmarks, 0f, 2f);

        Assert.Contains(1.23f, candidates);
        Assert.DoesNotContain(50f, candidates);
    }

    [Fact]
    public void GetSnapCandidates_ZeroBpm_ReturnsOnlyLandmarks()
    {
        var landmarks = new[] { 0.75f };
        var candidates = SnappingEngine.GetSnapCandidates(0f, landmarks, 0f, 2f);

        Assert.Equal(new[] { 0.75f }, candidates);
    }

    [Fact]
    public void Snap_HardMode_AlwaysSnapsToNearestCandidate()
    {
        float result = SnappingEngine.Snap(0.42f, SnappingMode.Hard, 120f, System.Array.Empty<float>());
        Assert.Equal(0.5f, result, 3);
    }

    [Fact]
    public void Snap_SoftMode_SnapsOnlyWithinThreshold()
    {
        // 0.49s is 0.01s from the 0.5s beat — within the default 0.015s threshold.
        float snapped = SnappingEngine.Snap(0.49f, SnappingMode.Soft, 120f, System.Array.Empty<float>());
        Assert.Equal(0.5f, snapped, 3);

        // 0.42s is 0.08s away — outside the threshold, stays put.
        float unsnapped = SnappingEngine.Snap(0.42f, SnappingMode.Soft, 120f, System.Array.Empty<float>());
        Assert.Equal(0.42f, unsnapped, 3);
    }

    [Fact]
    public void Snap_FreeMode_NeverSnaps()
    {
        float result = SnappingEngine.Snap(0.5001f, SnappingMode.Free, 120f, System.Array.Empty<float>());
        Assert.Equal(0.5001f, result, 4);
    }
}
