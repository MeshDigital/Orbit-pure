using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Regression tests for "I started a track and it kept playing after I switched albums / played
/// another track / opened Cue Forge — it could not be stopped".
///
/// Cause (verified against NAudio 2.2.1 on Windows): a WASAPI output raises
/// <see cref="IWavePlayer.PlaybackStopped"/> <em>synchronously from inside Stop()</em>. The service
/// stops a deck by disposing it, so on a track change the end-of-track handler ran while that deck
/// was still <c>_current</c>; it read a deliberate stop as "the track ended", promoted the
/// preloaded deck and started it at full level. <see cref="AudioPlayerService.Stop"/> then cleared
/// <c>_current</c>, leaving the promoted deck playing with nothing referencing it — no pause, stop
/// or track change could reach it again.
///
/// <see cref="FakeOutput"/> reproduces both output behaviours: WASAPI (event raised inside Stop())
/// and WaveOutEvent (event raised after Stop() returns, on another thread).
/// </summary>
public class AudioPlayerServiceDeckLifecycleTests : IDisposable
{
    private readonly List<string> _files = new();
    private readonly List<FakeOutput> _outputs = new();

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try { File.Delete(file); } catch { /* the reader may still be closing */ }
        }
    }

    [Theory]
    [InlineData(true)]  // WASAPI: PlaybackStopped fires inside Stop()
    [InlineData(false)] // WaveOutEvent: PlaybackStopped fires after Stop() returns
    public void PlayingAnotherTrack_DoesNotStartOrLoseThePreloadedDeck(bool stopRaisesEventSynchronously)
    {
        using var sut = NewService(stopRaisesEventSynchronously);

        sut.Play(NewWav());
        sut.PreloadNext(NewWav());
        var preloaded = _outputs.Last();
        Assert.False(preloaded.WasEverPlayed, "preloading must not make sound");

        sut.Play(NewWav()); // the user picks a different track while one is playing

        // Give an asynchronous PlaybackStopped (WaveOutEvent) time to arrive and be ignored.
        Thread.Sleep(100);

        Assert.False(preloaded.WasEverPlayed, "the preloaded deck was started by the track change");
        Assert.Equal(1, _outputs.Count(o => o.IsPlaying)); // exactly the requested track
        Assert.True(sut.IsPlaying);

        sut.Stop();
        Assert.DoesNotContain(_outputs, o => o.IsPlaying);
        Assert.False(sut.IsPlaying);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stop_DoesNotPromoteThePreloadedDeck(bool stopRaisesEventSynchronously)
    {
        using var sut = NewService(stopRaisesEventSynchronously);

        sut.Play(NewWav());
        sut.PreloadNext(NewWav());
        var preloaded = _outputs.Last();

        sut.Stop();
        Thread.Sleep(100);

        Assert.False(preloaded.WasEverPlayed, "Stop() started the preloaded deck instead of stopping");
        Assert.DoesNotContain(_outputs, o => o.IsPlaying);
    }

    [Fact]
    public void EndOfTrack_StillPromotesThePreloadedDeck()
    {
        using var sut = NewService(stopRaisesEventSynchronously: true);

        bool advanced = false;
        sut.TrackAdvanced += (_, _) => advanced = true;

        sut.Play(NewWav());
        sut.PreloadNext(NewWav());
        var outgoing = _outputs[0];
        var preloaded = _outputs[1];

        outgoing.RaiseEndOfStream(); // what NAudio reports when the file runs out

        Assert.True(advanced, "a natural end of track must still hand over to the next track");
        Assert.True(preloaded.IsPlaying);
        Assert.True(sut.IsPlaying);
        Assert.False(outgoing.IsPlaying);
    }

    [Fact]
    public void Stop_AlsoStopsADeckThatLostItsReference()
    {
        using var sut = NewService(stopRaisesEventSynchronously: true);

        sut.Play(NewWav());
        var playing = _outputs.Single(o => o.IsPlaying);

        // Simulate any future code path that drops a playing deck without disposing it.
        Set(sut, "_current", null);

        sut.Stop();

        Assert.False(playing.IsPlaying, "a playing deck with no reference must still be stopped");
    }

    private AudioPlayerService NewService(bool stopRaisesEventSynchronously)
    {
        var sut = new AudioPlayerService(new AppConfig());
        // Drive nothing by timer: these tests are about the explicit play/stop calls.
        Get<System.Timers.Timer>(sut, "_timer")!.Stop();
        sut.OutputFactory = _ =>
        {
            var output = new FakeOutput(stopRaisesEventSynchronously);
            _outputs.Add(output);
            return output;
        };
        return sut;
    }

    private string NewWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orbit-deck-{Guid.NewGuid():N}.wav");
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(8000, 1)))
        {
            for (int i = 0; i < 8000 * 4; i++) writer.WriteSample(0f);
        }
        _files.Add(path);
        return path;
    }

    private static void Set(object o, string field, object? value) =>
        o.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(o, value);

    private static T? Get<T>(object o, string field) =>
        (T?)o.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(o);

    /// <summary>An output device that records what was asked of it; no sound, no device needed.</summary>
    private sealed class FakeOutput : IWavePlayer
    {
        private readonly bool _stopRaisesEventSynchronously;
        private bool _stopEventRaised;

        public FakeOutput(bool stopRaisesEventSynchronously)
            => _stopRaisesEventSynchronously = stopRaisesEventSynchronously;

        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
        public float Volume { get; set; } = 1f;
        public int PlayCount { get; private set; }
        public bool WasEverPlayed => PlayCount > 0;
        public bool IsPlaying => PlaybackState == PlaybackState.Playing;

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void Init(ISampleProvider sampleProvider) { }

        public void Init(IWaveProvider waveProvider) { }

        public WaveFormat OutputWaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(8000, 2);

        public void Play()
        {
            PlayCount++;
            PlaybackState = PlaybackState.Playing;
            _stopEventRaised = false;
        }

        public void Pause() => PlaybackState = PlaybackState.Paused;

        public void Stop()
        {
            if (PlaybackState == PlaybackState.Stopped) return;
            PlaybackState = PlaybackState.Stopped;
            if (_stopRaisesEventSynchronously) RaiseStopped();
            else _ = Task.Run(RaiseStopped);
        }

        /// <summary>What the device does when the file runs out.</summary>
        public void RaiseEndOfStream()
        {
            PlaybackState = PlaybackState.Stopped;
            RaiseStopped();
        }

        private void RaiseStopped()
        {
            lock (this)
            {
                if (_stopEventRaised) return;
                _stopEventRaised = true;
            }
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void Dispose() => Stop();
    }
}
