using System.Collections.Generic;
using SLSKDONET.Services.Rekordbox;
using Xunit;

namespace SLSKDONET.Tests.Services.Rekordbox;

/// <summary>
/// Covers <see cref="RekordboxPssiService.ConvertToPhraseSegments"/> — specifically that it
/// prefers ORBIT's own real per-beat timestamp grid over constant-BPM extrapolation when
/// available, since a fixed-BPM formula silently drifts on any track with genuine tempo
/// variation while the real grid doesn't.
/// </summary>
public class RekordboxPssiServiceTests
{
    [Fact]
    public void ConvertToPhraseSegments_UsesRealBeatGrid_WhenAvailable()
    {
        // Beat 1 and beat 33 in this synthetic grid do NOT line up with a constant 120 BPM
        // extrapolation from beat 1 (which would place beat 33 at 0.0 + 32*0.5 = 16.0s) — the
        // grid says 16.4s, simulating a track with slight tempo drift. If the conversion is
        // correctly using the grid, the segment should start at 16.4, not 16.0.
        var grid = new List<double> { 0.0 };
        for (int i = 1; i < 40; i++) grid.Add(grid[i - 1] + 0.5); // ~120 BPM baseline
        grid[32] = 16.4; // beat 33 (index 32) drifts slightly

        var phrases = new List<RekordboxPhraseEntry>
        {
            new(1, 1, 1),   // Intro
            new(2, 33, 5),  // Chorus -> Drop
        };

        var segments = RekordboxPssiService.ConvertToPhraseSegments(
            mood: 1, phrases: phrases, bpm: 120, downbeatAnchor: 0.0, beatGridSeconds: grid);

        Assert.Equal(2, segments.Count);
        Assert.Equal("Drop", segments[1].Label);
        Assert.Equal(16.4f, segments[1].Start, precision: 3);
    }

    [Fact]
    public void ConvertToPhraseSegments_FallsBackToConstantBpm_WhenGridMissing()
    {
        var phrases = new List<RekordboxPhraseEntry> { new(1, 33, 5) }; // Chorus -> Drop, beat 33

        var segments = RekordboxPssiService.ConvertToPhraseSegments(
            mood: 1, phrases: phrases, bpm: 120, downbeatAnchor: 0.0, beatGridSeconds: null);

        // 120 BPM => 0.5s/beat => beat 33 (index 32) = 16.0s
        Assert.Single(segments);
        Assert.Equal(16.0f, segments[0].Start, precision: 3);
    }

    [Fact]
    public void ConvertToPhraseSegments_FallsBackPastEndOfGrid()
    {
        // Grid only covers the first 10 beats; phrase at beat 33 must fall back to constant-BPM
        // rather than throwing or clamping incorrectly.
        var shortGrid = new List<double> { 0.0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5 };
        var phrases = new List<RekordboxPhraseEntry> { new(1, 33, 5) };

        var segments = RekordboxPssiService.ConvertToPhraseSegments(
            mood: 1, phrases: phrases, bpm: 120, downbeatAnchor: 0.0, beatGridSeconds: shortGrid);

        Assert.Single(segments);
        Assert.Equal(16.0f, segments[0].Start, precision: 3);
    }

    [Fact]
    public void ConvertToPhraseSegments_LowMood_MapsBridgeAndChorus()
    {
        var phrases = new List<RekordboxPhraseEntry>
        {
            new(1, 1, 1),  // Intro
            new(2, 65, 8), // Bridge -> Breakdown
            new(3, 97, 9), // Chorus -> Drop
        };

        var segments = RekordboxPssiService.ConvertToPhraseSegments(
            mood: 3, phrases: phrases, bpm: 120, downbeatAnchor: 0.0, beatGridSeconds: null);

        Assert.Equal(3, segments.Count);
        Assert.Equal("Breakdown", segments[1].Label);
        Assert.Equal("Drop", segments[2].Label);
    }
}
