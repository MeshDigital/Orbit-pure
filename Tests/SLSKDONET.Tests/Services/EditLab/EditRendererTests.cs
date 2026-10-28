using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Services.EditLab;
using Xunit;

namespace SLSKDONET.Tests.Services.EditLab;

/// <summary>Synthetic-source tests for the Edit Lab renderer: clicks, sines and bursts written to temp
/// WAVs, rendered through the real plan → renderer → file path, then measured.</summary>
public sealed class EditRendererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orbit-editlab-render", Guid.NewGuid().ToString("N"));

    public EditRendererTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* temp dir */ }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string WriteWav(string name, int rate, double seconds, Func<long, float> sample, int channels = 2)
    {
        var path = Path.Combine(_dir, name + ".wav");
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        long frames = (long)(seconds * rate);
        var buf = new float[channels];
        for (long f = 0; f < frames; f++)
        {
            float s = sample(f);
            for (int c = 0; c < channels; c++) buf[c] = s;
            w.WriteSamples(buf, 0, channels);
        }
        return path;
    }

    /// <summary>Single-sample clicks every <paramref name="intervalSeconds"/>, starting at 0.</summary>
    private string Clicks(string name, int rate, double seconds, double intervalSeconds) =>
        WriteWav(name, rate, seconds, f => f % (long)Math.Round(intervalSeconds * rate) == 0 ? 1f : 0f);

    private string Sine(string name, int rate, double seconds, double hz, float amp) =>
        WriteWav(name, rate, seconds, f => amp * (float)Math.Sin(2 * Math.PI * hz * f / rate));

    private static float[] ReadMono(string path)
    {
        using var r = new AudioFileReader(path);
        var all = new float[(int)(r.Length / 4)];
        int n = r.Read(all, 0, all.Length);
        return Enumerable.Range(0, n / 2).Select(i => all[i * 2]).ToArray();
    }

    private static EditProject Project(double bpm, string path, double sourceBpm, out EditClip clip, EditStem stem = EditStem.FullMix, int rate = 44100)
    {
        var source = new EditSource { TrackHash = "a", Title = "A", Bpm = sourceBpm, FilePath = path, SampleRate = rate };
        if (stem != EditStem.FullMix) source.StemPaths[stem] = path;
        clip = new EditClip { SourceHash = "a", Stem = stem, StartBar = 0, LengthBars = 2 };
        var project = new EditProject { Bpm = bpm, Sources = { source }, BaseTrackHash = "a" };
        project.Lanes.Add(new EditLane { Clips = { clip } });
        return project;
    }

    private async Task<float[]> Render(EditProject project, int rate = 44100, long start = 0, long? end = null)
    {
        var plan = EditRenderPlan.Compile(project, rate);
        Assert.Empty(plan.Problems);
        var output = Path.Combine(_dir, $"out-{Guid.NewGuid():N}.wav");
        var result = await EditOfflineRenderer.RenderAsync(plan, output, EditRenderFormat.Wav, start, end);
        Assert.Equal((end ?? plan.TotalFrames) - start, result.Frames);
        return ReadMono(output);
    }

    private static int[] PeakFrames(float[] mono, float threshold) =>
        Enumerable.Range(0, mono.Length).Where(i => mono[i] > threshold).ToArray();

    // ── timing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clip_StartsOnItsBar_SampleExact()
    {
        var src = Clicks("clicks", 44100, 10, 0.5);
        var project = Project(120, src, 120, out var clip);
        clip.StartBar = 2; // 4.0 s at 120 BPM
        clip.LengthBars = 1;

        var mono = await Render(project);
        Assert.Equal(6 * 44100, mono.Length);                 // ends with the clip: 3 bars
        var clicks = PeakFrames(mono, 0.5f);
        Assert.Equal(new[] { 176400, 198450, 220500, 242550 }, clicks);
        Assert.All(mono.Take(176400), s => Assert.Equal(0f, s));
    }

    [Fact]
    public async Task SourceInPoint_IsHonoured()
    {
        var src = WriteWav("ramp", 44100, 4, f => f == 66150 ? 1f : 0f); // click at 1.5 s
        var project = Project(120, src, 120, out var clip);
        clip.SourceInSeconds = 1.0;
        var clicks = PeakFrames(await Render(project), 0.5f);
        Assert.Equal(new[] { 22050 }, clicks);                 // 0.5 s into the clip
    }

    [Fact]
    public void Resampler_StretchKeepsClicksOnTheNewGrid()
    {
        // 120 → 126 BPM: a click every 22050 source frames must land every 21000 output frames.
        var src = Clicks("c120", 44100, 6, 0.5);
        using var reader = new AudioFileReader(src);
        var resampler = new StereoRateResampler(reader, 44100, 1.05, 0);
        var buf = new float[44100 * 2 * 4];
        int n = resampler.Read(buf, 0, buf.Length) / 2;
        var mono = Enumerable.Range(0, n).Select(i => buf[i * 2]).ToArray();
        var peaks = Enumerable.Range(0, mono.Length - 1)
            .Where(i => mono[i] > 0.3f && (i == 0 || mono[i] >= mono[i - 1]) && mono[i] >= mono[i + 1]).ToArray();
        Assert.True(peaks.Length >= 7);
        for (int k = 0; k < 7; k++) Assert.InRange(peaks[k], k * 21000 - 1, k * 21000 + 1);
    }

    [Fact]
    public async Task SampleRateConversion_KeepsTiming()
    {
        var src = WriteWav("48k", 48000, 3, f => f == 48000 ? 1f : 0f); // click at 1.0 s
        var project = Project(120, src, 120, out _, rate: 48000);
        var mono = await Render(project, rate: 44100);
        int peak = Array.IndexOf(mono, mono.Max());
        Assert.InRange(peak, 44099, 44101);
    }

    [Fact]
    public async Task KeyLockedStretch_StaysAlignedAfterLatencyCompensation()
    {
        // A 440 Hz burst starting at source 2.0 s; clip stretched 120 → 126 BPM (key-locked through
        // the SMB shifter). The burst must start at 2.0 / 1.05 s in the output.
        var src = WriteWav("burst", 44100, 8, f => f >= 88200 && f < 88200 + 22050 ? 0.5f * (float)Math.Sin(2 * Math.PI * 440 * f / 44100) : 0f);
        var project = Project(126, src, 120, out var clip);
        clip.LengthBars = 3;
        var mono = await Render(project);

        int onset = Enumerable.Range(0, mono.Length).First(i => Math.Abs(mono[i]) > 0.15f);
        double expected = 2.0 / 1.05 * 44100;
        Assert.InRange(onset, expected - 0.012 * 44100, expected + 0.012 * 44100);
    }

    // ── levels ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gain_AppliesInDecibels()
    {
        var src = Sine("sine", 44100, 6, 220, 0.5f);
        var project = Project(120, src, 120, out var clip);
        clip.GainDb = -6.0206;
        var mono = await Render(project);
        Assert.InRange(mono.Max(), 0.245f, 0.2505f);
    }

    [Fact]
    public async Task Fades_RampLinearly()
    {
        var src = WriteWav("dc", 44100, 6, _ => 0.8f);
        var project = Project(120, src, 120, out var clip);
        clip.FadeInBars = 1;   // 2 s
        clip.FadeOutBars = 0.5; // 1 s
        var mono = await Render(project);
        Assert.Equal(0f, mono[0]);
        Assert.InRange(mono[22050], 0.19f, 0.21f);   // 25 % into the fade-in
        Assert.InRange(mono[88200], 0.79f, 0.81f);   // fade-in done
        Assert.InRange(mono[3 * 44100 + 22050], 0.39f, 0.41f); // half way through the fade-out
        Assert.InRange(mono[^1], 0f, 0.001f);
    }

    [Fact]
    public async Task PitchShift_OctaveUp_DoublesFrequency()
    {
        var src = Sine("a440", 44100, 6, 440, 0.5f);
        var project = Project(120, src, 120, out var clip);
        clip.KeyMode = ClipKeyMode.Manual;
        clip.ManualSemitones = 12;
        var mono = await Render(project);
        var window = mono.Skip(44100).Take(44100).ToArray();
        int crossings = Enumerable.Range(1, window.Length - 1).Count(i => window[i - 1] < 0 && window[i] >= 0);
        Assert.InRange(crossings, 860, 900);
    }

    [Fact]
    public async Task MuteAndSolo_DecideWhatIsHeard()
    {
        var a = WriteWav("a", 44100, 6, _ => 0.25f);
        var b = WriteWav("b", 44100, 6, _ => 0.5f);
        var project = Project(120, a, 120, out var clipA);
        project.Sources.Add(new EditSource { TrackHash = "b", Title = "B", Bpm = 120, FilePath = b });
        var laneB = new EditLane { Clips = { new EditClip { SourceHash = "b", LengthBars = 2 } } };
        project.Lanes.Add(laneB);

        Assert.InRange((await Render(project))[1000], 0.74f, 0.76f);
        laneB.Solo = true;
        Assert.InRange((await Render(project))[1000], 0.49f, 0.51f);
        laneB.Solo = false;
        clipA.Muted = true;
        Assert.InRange((await Render(project))[1000], 0.49f, 0.51f);
    }

    // ── plan / range / failures ──────────────────────────────────────────────

    [Fact]
    public async Task LoopRange_RendersOnlyThatRange()
    {
        var src = Clicks("loopclicks", 44100, 10, 0.5);
        var project = Project(120, src, 120, out var clip);
        clip.LengthBars = 4;
        var plan = EditRenderPlan.Compile(project, 44100);
        long start = plan.BarToFrame(1), end = plan.BarToFrame(2);
        var mono = await Render(project, start: start, end: end);
        Assert.Equal(88200, mono.Length);
        Assert.Equal(new[] { 0, 22050, 44100, 66150 }, PeakFrames(mono, 0.5f));
    }

    [Fact]
    public void SeekingMidClip_MatchesTheContinuousRender()
    {
        var src = Sine("seek", 44100, 6, 330, 0.4f);
        var project = Project(120, src, 120, out _);
        var plan = EditRenderPlan.Compile(project, 44100);

        var full = new float[44100 * 2 * 3];
        using (var r = new EditTimelineRenderer(plan)) r.Read(full, 0, full.Length);
        var tail = new float[44100 * 2];
        using (var r = new EditTimelineRenderer(plan, 44100 + 123)) r.Read(tail, 0, tail.Length);

        for (int i = 0; i < 2000; i++) Assert.Equal(full[(44100 + 123) * 2 + i], tail[i], 5);
    }

    [Fact]
    public void Instrumental_ReadsDrumsBassAndOther()
    {
        var src = WriteWav("stem", 44100, 2, _ => 0.1f);
        var project = Project(120, src, 120, out var clip);
        var source = project.Sources[0];
        source.StemPaths[EditStem.Drums] = src;
        source.StemPaths[EditStem.Bass] = src;
        source.StemPaths[EditStem.Other] = src;
        clip.Stem = EditStem.Instrumental;
        var plan = EditRenderPlan.Compile(project, 44100);
        Assert.Equal(3, plan.Clips.Count);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public async Task MissingStem_IsAProblem_AndRenderRefuses()
    {
        var src = WriteWav("full", 44100, 2, _ => 0.1f);
        var project = Project(120, src, 120, out var clip);
        clip.Stem = EditStem.Vocals;
        var plan = EditRenderPlan.Compile(project, 44100);
        Assert.Contains(plan.Problems, p => p.Contains("vocals stem isn't separated", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EditOfflineRenderer.RenderAsync(plan, Path.Combine(_dir, "x.wav"), EditRenderFormat.Wav));
    }

    [Fact]
    public async Task Cancel_DeletesThePartialFile()
    {
        var src = WriteWav("long", 44100, 30, _ => 0.1f);
        var project = Project(120, src, 120, out var clip);
        clip.LengthBars = 14;
        var plan = EditRenderPlan.Compile(project, 44100);
        var output = Path.Combine(_dir, "cancelled.wav");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p > 0.1) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EditOfflineRenderer.RenderAsync(plan, output, EditRenderFormat.Wav, progress: progress, ct: cts.Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task NegativeInPoint_PadsWithSilence()
    {
        var src = WriteWav("neg", 44100, 4, f => f == 0 ? 1f : 0f);
        var project = Project(120, src, 120, out var clip);
        clip.SourceInSeconds = -0.25;
        Assert.Equal(new[] { 11025 }, PeakFrames(await Render(project), 0.5f));
    }

    [Fact]
    public async Task Clipping_IsClampedAndCounted()
    {
        var src = WriteWav("hot", 44100, 3, _ => 0.9f);
        var project = Project(120, src, 120, out var clip);
        clip.GainDb = 6;
        var plan = EditRenderPlan.Compile(project, 44100);
        var result = await EditOfflineRenderer.RenderAsync(plan, Path.Combine(_dir, "hot-out.wav"), EditRenderFormat.Wav);
        Assert.True(result.ClippedSamples > 0);
        Assert.True(result.Peak > 1.0f);
        Assert.InRange(ReadMono(result.OutputPath).Max(), 0.999f, 1.0f);
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _action;
        public SyncProgress(Action<double> action) => _action = action;
        public void Report(double value) => _action(value);
    }
}
