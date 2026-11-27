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

        var trackA = NewWav();
        var trackB = NewWav();
        var trackC = NewWav();

        sut.Play(trackA);
        sut.PreloadNext(trackB);
        // MX1 (#600): Unified mix bus opens exactly ONE shared audio output device across decks.
        Assert.Single(_outputs);

        sut.Play(trackC); // the user picks a different track while one is playing

        // Give an asynchronous PlaybackStopped (WaveOutEvent) time to arrive and be ignored.
        Thread.Sleep(100);

        Assert.Single(_outputs);
        Assert.Equal(trackC, sut.LoadedFilePath);
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
        Assert.Single(_outputs);

        sut.Stop();
        Thread.Sleep(100);

        Assert.Null(sut.LoadedFilePath);
        Assert.DoesNotContain(_outputs, o => o.IsPlaying);
        Assert.False(sut.IsPlaying);
    }

    [Fact]
    public void EndOfTrack_StillPromotesThePreloadedDeck()
    {
        using var sut = NewService(stopRaisesEventSynchronously: true);

        var advanced = new ManualResetEventSlim(false);
        sut.TrackAdvanced += (_, _) => advanced.Set();

        var trackA = NewWav(seconds: 0.15);
        var trackB = NewWav(seconds: 0.5);
        sut.Play(trackA);
        sut.PreloadNext(trackB);
        Assert.Single(_outputs);

        // Drain the single shared mix bus until Track A runs out and Track B is promoted.
        var buf = new float[2048];
        var source = _outputs[0].Source!;
        for (int i = 0; i < 60 && !advanced.IsSet; i++)
            source.Read(buf, 0, buf.Length);

        Assert.True(advanced.Wait(TimeSpan.FromSeconds(2)), "a natural end of track must still hand over to the next track");
        Assert.Single(_outputs);
        Assert.Equal(trackB, sut.LoadedFilePath);
        Assert.True(sut.IsPlaying);
    }

    [Fact]
    public void UnifiedBus_CrossfadeUsesSingleOutput_AndPromotesOnExactSampleClock()
    {
        using var sut = NewService(stopRaisesEventSynchronously: true);

        var started = new ManualResetEventSlim(false);
        var ended = new ManualResetEventSlim(false);
        var advanced = new ManualResetEventSlim(false);
        sut.CrossfadeStarted += (_, _) => started.Set();
        sut.CrossfadeEnded += (_, _) => ended.Set();
        sut.TrackAdvanced += (_, _) => advanced.Set();

        var trackA = NewWav(seconds: 1.0, amplitude: 0.25f);
        var trackB = NewWav(seconds: 1.0, amplitude: 0.75f);
        var transition = new SLSKDONET.Models.Timeline.TransitionModel
        {
            Type = SLSKDONET.Models.Timeline.TransitionType.EqSwap,
            DurationBeats = 1, // 0.5 s at 120 BPM
            EqSwapLow = true,
            EqHardLowSwap = true,
        };

        sut.Play(trackA);
        sut.PreloadNext(trackB, transition: transition, transitionBpm: 120.0, sourceTriggerSeconds: 0.25, presetName: "Blend");
        Assert.Single(_outputs);

        var source = _outputs[0].Source!;
        var buf = new float[2048];
        for (int i = 0; i < 120 && !advanced.IsSet; i++)
            source.Read(buf, 0, buf.Length);

        Assert.True(started.Wait(TimeSpan.FromSeconds(2)), "CrossfadeStarted should fire on the scheduled sample");
        Assert.True(ended.Wait(TimeSpan.FromSeconds(2)), "CrossfadeEnded should fire when transition finishes");
        Assert.True(advanced.Wait(TimeSpan.FromSeconds(2)), "TrackAdvanced should fire after promotion");
        Assert.Single(_outputs);
        Assert.Equal(trackB, sut.LoadedFilePath);
        Assert.True(sut.IsPlaying);
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
        sut.OutputFactory = source =>
        {
            var output = new FakeOutput(stopRaisesEventSynchronously);
            output.Init(source);
            _outputs.Add(output);
            return output;
        };
        return sut;
    }

    private string NewWav(double seconds = 4.0, float amplitude = 0f)
    {
        var path = Path.Combine(Path.GetTempPath(), $"orbit-deck-{Guid.NewGuid():N}.wav");
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(8000, 1)))
        {
            int samples = (int)Math.Round(8000 * seconds);
            for (int i = 0; i < samples; i++) writer.WriteSample(amplitude);
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

        public ISampleProvider? Source { get; private set; }
        public void Init(ISampleProvider sampleProvider) => Source = sampleProvider;

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
