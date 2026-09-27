using SLSKDONET.Configuration;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// IsVisualizerActive gates the FFT/spectrum computation in AudioPlayerService's playback chain
/// (previously ran unconditionally for every playing track, regardless of whether any visualizer
/// control existed) — nothing ever called the setter to turn it on, so the gate itself, once
/// added, would have silently disabled the visualizer entirely without this wiring. Covers just
/// the reference-counting semantics: multiple simultaneously-attached visualizer controls (a mini
/// one and a full one, or two of the same) must not have one's detach turn the flag off while the
/// other is still attached.
/// </summary>
public class AudioPlayerServiceVisualizerGatingTests
{
    [Fact]
    public void IsVisualizerActive_FalseByDefault()
    {
        var service = new AudioPlayerService(new AppConfig());

        Assert.False(service.IsVisualizerActive);
    }

    [Fact]
    public void NotifyVisualizerAttached_TurnsActiveOn()
    {
        var service = new AudioPlayerService(new AppConfig());

        service.NotifyVisualizerAttached();

        Assert.True(service.IsVisualizerActive);
    }

    [Fact]
    public void NotifyVisualizerDetached_MatchingSingleAttach_TurnsActiveOff()
    {
        var service = new AudioPlayerService(new AppConfig());

        service.NotifyVisualizerAttached();
        service.NotifyVisualizerDetached();

        Assert.False(service.IsVisualizerActive);
    }

    [Fact]
    public void TwoSimultaneousAttaches_OneDetaching_StaysActiveForTheOtherStillAttached()
    {
        var service = new AudioPlayerService(new AppConfig());

        service.NotifyVisualizerAttached(); // mini visualizer
        service.NotifyVisualizerAttached(); // full-screen visualizer, opened while mini still visible

        service.NotifyVisualizerDetached(); // mini closes

        Assert.True(service.IsVisualizerActive);

        service.NotifyVisualizerDetached(); // full-screen closes too

        Assert.False(service.IsVisualizerActive);
    }

    [Fact]
    public void UnmatchedExtraDetach_DoesNotCorruptFutureAttachDetachPairs()
    {
        var service = new AudioPlayerService(new AppConfig());

        // A stray detach with no matching attach (shouldn't happen if every control pairs its
        // calls correctly, but must not permanently break the counter if it ever does).
        service.NotifyVisualizerDetached();
        Assert.False(service.IsVisualizerActive);

        service.NotifyVisualizerAttached();
        Assert.True(service.IsVisualizerActive);

        service.NotifyVisualizerDetached();
        Assert.False(service.IsVisualizerActive);
    }
}
