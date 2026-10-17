using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Services.EditLab;
using SLSKDONET.ViewModels.EditLab;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>Drives the Edit Lab view model end to end with a fake library, a temp project folder,
/// a fake output device and a fake shared player.</summary>
public sealed class EditLabViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orbit-editlab-vm", Guid.NewGuid().ToString("N"));
    private readonly FakeSources _sources;
    private readonly FakeGate _gate = new();
    private readonly FakeDevice _device = new();
    private readonly EditLabViewModel _vm;

    public EditLabViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _sources = new FakeSources(_dir);
        _vm = new EditLabViewModel(new EditProjectStore(rootDirectory: _dir), _sources, new EditPreviewEngine(() => _device),
            _gate, ui: a => { a(); return Task.CompletedTask; });
    }

    public void Dispose()
    {
        _vm.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* temp dir */ }
    }

    private async Task<EditSourceItemViewModel> ProjectWithTrack(string hash = "a", double bpm = 120)
    {
        await _vm.NewProjectCommand.Execute();
        _sources.Bpm[hash] = bpm;
        await _vm.AddTrackAsync(hash, "Track " + hash, "Artist", null);
        return _vm.Sources.Single(s => s.Hash == hash);
    }

    [Fact]
    public async Task FirstTrack_BecomesTheBase_AndSetsTempoAndKey()
    {
        var item = await ProjectWithTrack("a", 126);
        Assert.True(_vm.HasProject);
        Assert.True(item.IsBase);
        Assert.Equal("126.0", _vm.BpmText);
        Assert.Equal("8A", _vm.KeyText);
        Assert.Equal(StemAvailability.NotSeparated, item.Stems);
    }

    [Fact]
    public async Task AddClip_SelectsIt_AndUndoRedoWork()
    {
        var item = await ProjectWithTrack();
        item.AddClipCommand.Execute("FullMix").Subscribe();

        Assert.Single(_vm.Lanes);
        Assert.NotNull(_vm.SelectedClip);
        Assert.True(_vm.HasClips);

        _vm.UndoCommand.Execute().Subscribe();
        Assert.False(_vm.HasClips);
        Assert.Null(_vm.SelectedClip);
        _vm.RedoCommand.Execute().Subscribe();
        Assert.True(_vm.HasClips);
    }

    [Fact]
    public async Task InspectorEdits_AreUndoable_AndSliderDragsCoalesce()
    {
        var item = await ProjectWithTrack();
        item.AddClipCommand.Execute("FullMix").Subscribe();
        var clip = _vm.SelectedClip!;

        clip.GainDb = -1;
        clip.GainDb = -2;
        clip.GainDb = -3;
        Assert.Equal(-3, _vm.Project!.AllClips.Single().GainDb);

        _vm.UndoCommand.Execute().Subscribe();
        Assert.Equal(0, _vm.Project!.AllClips.Single().GainDb);
        Assert.Equal(0, _vm.SelectedClip!.GainDb);
    }

    [Fact]
    public async Task DraggingAClip_IsOneUndoStep()
    {
        var item = await ProjectWithTrack();
        item.AddClipCommand.Execute("FullMix").Subscribe();
        var id = _vm.SelectedClipId!.Value;

        _vm.BeginClipGesture();
        _vm.MoveClipLive(id, 1, 0);
        _vm.MoveClipLive(id, 2, 0);
        _vm.MoveClipLive(id, 3.25, 0);
        _vm.EndClipGesture();
        Assert.Equal(3.25, _vm.Project!.FindClip(id)!.StartBar);

        _vm.UndoCommand.Execute().Subscribe();
        Assert.Equal(0, _vm.Project!.FindClip(id)!.StartBar);
    }

    [Fact]
    public async Task StemClipWithoutStems_IsReportedAsAProblem()
    {
        var item = await ProjectWithTrack();
        item.AddClipCommand.Execute("Vocals").Subscribe();
        Assert.True(_vm.HasProblems);
        Assert.Contains("vocals stem isn't separated", _vm.Problems, StringComparison.Ordinal);
        Assert.True(_vm.SelectedClip!.StemMissing);
    }

    [Fact]
    public async Task Separation_FillsStems_AndClearsTheProblem()
    {
        var item = await ProjectWithTrack();
        item.AddClipCommand.Execute("Vocals").Subscribe();
        await _vm.SeparateAsync(item);
        Assert.Equal(StemAvailability.Ready, item.Stems);
        Assert.False(_vm.HasProblems);
    }

    [Fact]
    public async Task Preview_PausesTheSharedPlayer_AndStopsWhenItIsResumed()
    {
        var item = await ProjectWithTrack();
        item.AddClipCommand.Execute("FullMix").Subscribe();
        _gate.Playing.OnNext(true);

        await _vm.PlayPauseCommand.Execute();
        Assert.True(_vm.IsPlaying);
        Assert.True(_gate.PauseCalled);
        Assert.Equal(PlaybackState.Playing, _device.PlaybackState);

        _gate.Playing.OnNext(true); // the user pressed play on the main player
        Assert.False(_vm.IsPlaying);
        Assert.Contains("main player", _vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualTempo_OverridesTheBase_AndAutoMatchToggles()
    {
        var item = await ProjectWithTrack("a", 120);
        item.AddClipCommand.Execute("FullMix").Subscribe();
        _vm.BpmText = "126";
        Assert.True(_vm.Project!.BpmIsManual);
        Assert.Equal("+5.0 %", _vm.SelectedClip!.StretchDisplay);

        _vm.AutoMatchTempo = false;
        Assert.Equal("Native tempo", _vm.SelectedClip!.StretchDisplay);
        _vm.SelectedClip!.TempoAlwaysFollow = true;
        Assert.Equal("+5.0 %", _vm.SelectedClip!.StretchDisplay);
    }

    [Fact]
    public async Task Projects_Autosave_AndReopen()
    {
        var item = await ProjectWithTrack();
        item.AddClipCommand.Execute("FullMix").Subscribe();
        _vm.ProjectName = "My intro";
        var id = _vm.Project!.Id;
        var path = Path.Combine(_dir, "projects", id.ToString("N") + ".json");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(path) && File.ReadAllText(path).Contains("My intro"), 5000));

        await _vm.CloseProjectCommand.Execute();
        Assert.False(_vm.HasProject);
        Assert.Contains(_vm.RecentProjects, r => r.Id == id);

        await _vm.OpenProjectAsync(id);
        Assert.Equal("My intro", _vm.ProjectName);
        Assert.Single(_vm.Project!.AllClips);
    }

    [Fact]
    public async Task Template_DefaultsTracks_AppliesAsOneUndoStep()
    {
        await ProjectWithTrack("a");
        Assert.False(_vm.CanApplyTemplate); // custom intro needs a second track
        _sources.Bpm["b"] = 124;
        await _vm.AddTrackAsync("b", "Track b", "Artist", null);

        Assert.Equal("a", _vm.TemplateTrackA!.Hash);
        Assert.Equal("b", _vm.TemplateTrackB!.Hash);
        _vm.TemplateBars = 8;
        _vm.ApplyTemplateCommand.Execute().Subscribe();
        Assert.Equal(3, _vm.Lanes.Count);
        Assert.True(_vm.HasProblems); // drums/vocals stems aren't separated yet

        _vm.UndoCommand.Execute().Subscribe();
        Assert.False(_vm.HasClips);
    }

    // ── fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeSources : EditLabSourceService
    {
        private readonly string _dir;
        public readonly Dictionary<string, double> Bpm = new();

        public FakeSources(string dir) : base(null, null, null) => _dir = dir;

        private string Wav(string name)
        {
            var path = Path.Combine(_dir, name + ".wav");
            if (File.Exists(path)) return path;
            using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
            var block = new float[44100 * 2];
            for (int i = 0; i < block.Length; i++) block[i] = 0.1f;
            for (int s = 0; s < 40; s++) w.WriteSamples(block, 0, block.Length);
            return path;
        }

        public override bool CanSeparate => true;

        public override Task<EditSource> LoadSourceAsync(string trackHash, string? title = null, string? artist = null, string? knownFilePath = null, CancellationToken ct = default) =>
            Task.FromResult(new EditSource
            {
                TrackHash = trackHash, Title = title ?? trackHash, Artist = artist ?? "", FilePath = Wav(trackHash),
                Bpm = Bpm.TryGetValue(trackHash, out var b) ? b : 120, Key = "8A", DurationSeconds = 40, SampleRate = 44100,
            });

        public override Task<bool> RefreshStemsAsync(EditSource source, CancellationToken ct = default) => Task.FromResult(HasAllStems(source));

        public override Task<IReadOnlyDictionary<EditStem, string>> SeparateAsync(EditSource source, IProgress<float>? progress, CancellationToken ct)
        {
            var map = new Dictionary<EditStem, string>();
            foreach (var stem in new[] { EditStem.Vocals, EditStem.Drums, EditStem.Bass, EditStem.Other })
                map[stem] = Wav($"{source.TrackHash}-{stem}");
            foreach (var (k, v) in map) source.StemPaths[k] = v;
            return Task.FromResult<IReadOnlyDictionary<EditStem, string>>(map);
        }

        public override string? ResolvePlayablePath(EditSource source, EditStem stem) => EditRenderPlan.DefaultAudioPath(source, stem);
    }

    private sealed class FakeGate : IEditLabPlayerGate
    {
        public readonly BehaviorSubject<bool> Playing = new(false);
        public bool PauseCalled;
        public bool IsPlaying => Playing.Value;
        public IObservable<bool> IsPlayingChanged => Playing;
        public void Pause() => PauseCalled = true;
    }

    private sealed class FakeDevice : IWavePlayer
    {
        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
        public float Volume { get => 1f; set => throw new InvalidOperationException("Never set IWavePlayer.Volume"); }
        public WaveFormat OutputWaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider waveProvider) { }
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Dispose() { }
    }
}
