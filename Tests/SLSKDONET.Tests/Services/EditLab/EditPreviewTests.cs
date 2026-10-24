using System;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Services.EditLab;
using Xunit;

namespace SLSKDONET.Tests.Services.EditLab;

public sealed class EditPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orbit-editlab-preview", Guid.NewGuid().ToString("N"));

    public EditPreviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* temp dir */ }
    }

    private EditRenderPlan ClickPlan(int bars)
    {
        var path = Path.Combine(_dir, "clicks.wav");
        using (var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)))
        {
            for (long f = 0; f < 44100L * 20; f++)
            {
                float s = f % 22050 == 0 ? (f % 88200 == 0 ? 1f : 0.6f) : 0f; // accent on each bar
                w.WriteSamples(new[] { s, s }, 0, 2);
            }
        }
        var project = new EditProject { Bpm = 120, Sources = { new EditSource { TrackHash = "a", Bpm = 120, FilePath = path } } };
        project.Lanes.Add(new EditLane { Clips = { new EditClip { SourceHash = "a", LengthBars = bars } } });
        return EditRenderPlan.Compile(project, 44100);
    }

    /// <summary>Reads like an audio device would, keeping only real (non-underrun) audio.</summary>
    private static float[] Pull(EditPreviewStream stream, int frames)
    {
        var all = new float[frames * 2];
        var block = new float[1024 * 2];
        int got = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (got < frames && DateTime.UtcNow < deadline)
        {
            int take = Math.Min(1024, frames - got);
            long before = stream.FramesDelivered;
            stream.Read(block, 0, take * 2);
            int produced = (int)(stream.FramesDelivered - before);
            if (produced == 0) { Thread.Sleep(1); continue; }
            Array.Copy(block, 0, all, got * 2, produced * 2);
            got += produced;
        }
        Assert.Equal(frames, got);
        return all;
    }

    private static int[] Clicks(float[] stereo, float threshold) =>
        Enumerable.Range(0, stereo.Length / 2).Where(i => stereo[i * 2] > threshold).ToArray();

    [Fact]
    public void Stream_PlaysTheSameAudioAsTheOfflineRender()
    {
        var plan = ClickPlan(4);
        using var stream = new EditPreviewStream(44100);
        stream.Start(plan, 1000, loop: null);
        var live = Pull(stream, 44100);

        var offline = new float[44100 * 2];
        using (var r = new EditTimelineRenderer(plan, 1000)) r.Read(offline, 0, offline.Length);
        Assert.Equal(offline, live);
        Assert.Equal(new[] { 22050 - 1000, 44100 - 1000 }, Clicks(live, 0.5f));
    }

    [Fact]
    public void Stream_LoopsTheRegion()
    {
        var plan = ClickPlan(8);
        using var stream = new EditPreviewStream(44100);
        long start = plan.BarToFrame(2), end = plan.BarToFrame(3); // one bar: 88200 frames
        stream.Start(plan, start, (start, end));
        var live = Pull(stream, 88200 * 2 + 100);
        Assert.Equal(new[] { 0, 88200, 176400 }, Clicks(live, 0.9f)); // the bar accent repeats
    }

    [Fact]
    public void Stream_SeekJumpsWithoutStaleAudio()
    {
        var plan = ClickPlan(8);
        using var stream = new EditPreviewStream(44100);
        stream.Start(plan, 0, null);
        Pull(stream, 4096);
        stream.Start(plan, plan.BarToFrame(4) - 10, null);
        var after = Pull(stream, 100);
        Assert.Equal(new[] { 10 }, Clicks(after, 0.9f));
        Assert.Equal(plan.BarToFrame(4) + 90, stream.Position);
    }

    [Fact]
    public void Stream_SignalsTheEnd()
    {
        var plan = ClickPlan(1);
        using var stream = new EditPreviewStream(44100);
        using var ended = new ManualResetEventSlim();
        stream.Ended += () => ended.Set();
        stream.Start(plan, plan.TotalFrames - 2000, null);
        var buf = new float[4096];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ended.IsSet && DateTime.UtcNow < deadline) { stream.Read(buf, 0, buf.Length); Thread.Sleep(2); }
        Assert.True(ended.IsSet);
    }

    [Fact]
    public void Engine_UsesItsOwnDevice_AndNeverTouchesDeviceVolume()
    {
        var plan = ClickPlan(2);
        var device = new FakeDevice();
        using var engine = new EditPreviewEngine(() => device);
        engine.Volume = 0.5f;
        engine.Play(plan, 0, null);
        Assert.True(device.Initialised);
        Assert.Equal(PlaybackState.Playing, device.PlaybackState);
        Assert.True(engine.IsPlaying);

        engine.Stop();
        Assert.False(engine.IsPlaying);
        Assert.True(SpinWait.SpinUntil(() => device.Disposed, 2000));
        Assert.False(device.VolumeWasSet);
    }

    private sealed class FakeDevice : IWavePlayer
    {
        public bool Initialised, Disposed, VolumeWasSet;
        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
        public float Volume { get => 1f; set => VolumeWasSet = true; }
        public WaveFormat OutputWaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider waveProvider) => Initialised = true;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Dispose() => Disposed = true;
    }
}
