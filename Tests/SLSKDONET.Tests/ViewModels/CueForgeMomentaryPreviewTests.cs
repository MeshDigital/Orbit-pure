using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Unit tests for #617: CDJ/Rekordbox momentary hold-to-play cue &amp; hot-cue pad preview.
/// </summary>
public sealed class CueForgeMomentaryPreviewTests
{
    [Fact]
    public async Task HoldC_WhenPausedAtCue_PlaysAndSnapsBackOnRelease()
    {
        var transport = new FakeCueForgeTransport("track.flac", lengthSeconds: 200);
        using var vm = CreateVm(transport, new[]
        {
            new CuePointEntity { TrackUniqueHash = "h1", TimestampInSeconds = 32, Label = "Drop", SlotIndex = 0 }
        });
        await vm.LoadTrackAsync("h1", "Test", "Artist");

        // 1. Paused away from cue (at 32s): pressing C sets transport cue without playing.
        vm.CurrentPlayPosition = 32.0;
        vm.BeginMomentaryCuePreview();
        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsMomentaryCuePreview);

        // 2. Now paused AT transport cue: pressing & holding C starts momentary preview.
        vm.BeginMomentaryCuePreview();
        Assert.True(vm.IsPlaying);
        Assert.True(vm.IsMomentaryCuePreview);

        // Simulate playback advancing 3 seconds while held.
        vm.CurrentPlayPosition = 35.0;

        // 3. Releasing C pauses and snaps back to 32.0s.
        vm.EndMomentaryCuePreview();
        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsMomentaryCuePreview);
        Assert.Equal(32.0, vm.CurrentPlayPosition, precision: 2);
    }

    [Fact]
    public async Task HoldC_ThenPressPlay_LatchesContinuousPlayback()
    {
        var transport = new FakeCueForgeTransport("track.flac", lengthSeconds: 200);
        using var vm = CreateVm(transport, Array.Empty<CuePointEntity>());
        await vm.LoadTrackAsync("h1", "Test", "Artist");

        vm.CurrentPlayPosition = 16.0;
        vm.BeginMomentaryCuePreview(); // sets cue at 16s
        vm.BeginMomentaryCuePreview(); // starts momentary preview
        Assert.True(vm.IsPlaying);
        Assert.True(vm.IsMomentaryCuePreview);

        // Handoff: press Play while holding C.
        vm.PlayPauseCommand.Execute().Subscribe();
        Assert.False(vm.IsMomentaryCuePreview);
        Assert.True(vm.IsPlaying);

        // Releasing C now keeps playing!
        vm.EndMomentaryCuePreview();
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public async Task HoldPad_WhenPaused_PreviewsAndSnapsBackOnRelease()
    {
        var transport = new FakeCueForgeTransport("track.flac", lengthSeconds: 200);
        using var vm = CreateVm(transport, new[]
        {
            new CuePointEntity { TrackUniqueHash = "h1", TimestampInSeconds = 48, Label = "Drop", SlotIndex = 0 }
        });
        await vm.LoadTrackAsync("h1", "Test", "Artist");

        var padA = vm.Session.HotCuePads[0];
        Assert.NotNull(padA.Cue);

        vm.BeginMomentaryPadPreview(padA);
        Assert.True(vm.IsPlaying);
        Assert.True(vm.IsMomentaryCuePreview);
        Assert.Equal(48.0, vm.CurrentPlayPosition, precision: 2);

        vm.CurrentPlayPosition = 51.0;
        vm.EndMomentaryCuePreview();
        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsMomentaryCuePreview);
        Assert.Equal(48.0, vm.CurrentPlayPosition, precision: 2);
    }

    private static CueForgeViewModel CreateVm(FakeCueForgeTransport transport, IReadOnlyList<CuePointEntity> cues)
    {
        var cueService = new Mock<ICuePointService>();
        cueService.Setup(s => s.GetByTrackIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CuePointEntity>(cues));

        return new CueForgeViewModel(
            cueService.Object,
            transport,
            new AppConfig(),
            ui: a => { a(); return Task.CompletedTask; });
    }

    private sealed class FakeCueForgeTransport : ICueForgeTransport
    {
        private readonly Subject<bool> _isPlayingChanged = new();
        private readonly Subject<(double, double)> _posChanged = new();
        private readonly Subject<CueForgeTrackRef?> _trackChanged = new();
        private string? _loadedPath;

        public FakeCueForgeTransport(string filePath, double lengthSeconds)
        {
            _loadedPath = filePath;
            LengthSeconds = lengthSeconds;
        }

        public bool IsPlaying { get; private set; }
        public string? CurrentFilePath => _loadedPath;
        public string? LoadedFilePath => _loadedPath;
        public string? CurrentTrackHash => "h1";
        public string Title => "Test";
        public string Artist => "Artist";
        public double PositionFraction { get; private set; }
        public double LengthSeconds { get; }

        public IObservable<bool> IsPlayingChanged => _isPlayingChanged;
        public IObservable<(double Fraction, double LengthSeconds)> PositionChanged => _posChanged;
        public IObservable<CueForgeTrackRef?> CurrentTrackChanged => _trackChanged;

        public void LoadPaused(string filePath, string title, string artist)
        {
            _loadedPath = filePath;
            IsPlaying = false;
        }

        public void Play()
        {
            IsPlaying = true;
            _isPlayingChanged.OnNext(true);
        }

        public void Pause()
        {
            IsPlaying = false;
            _isPlayingChanged.OnNext(false);
        }

        public void SeekFraction(double fraction) => PositionFraction = Math.Clamp(fraction, 0.0, 1.0);
        public void SetMetronome(IReadOnlyList<double>? beatTimes, double downbeatSeconds, bool enabled) { }
        public Task<string?> ResolveFilePathAsync(string trackHash) => Task.FromResult(_loadedPath);
    }
}
