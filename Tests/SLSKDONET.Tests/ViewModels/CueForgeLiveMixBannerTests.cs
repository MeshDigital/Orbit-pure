using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public sealed class CueForgeLiveMixBannerTests
{
    [Fact]
    public async Task IncomingMixBanner_ShowsDuringCrossfade_AndOpensIncomingTrack()
    {
        var transport = new MixBannerTransport();
        var cueService = new Mock<ICuePointService>();
        cueService.Setup(s => s.GetByTrackIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CuePointEntity>());

        using var vm = new CueForgeViewModel(
            cueService.Object,
            transport,
            new AppConfig(),
            ui: a => { a(); return Task.CompletedTask; });

        await vm.LoadTrackAsync("track-1", "First Track", "Artist A");
        Assert.False(vm.IsMixingIncoming);

        // 2nd track starts playing on top of 1st track during Auto-Mix crossfade.
        transport.IncomingMix.OnNext(new CueForgeIncomingMixInfo(
            IsActive: true,
            ProgressPercent: 42.0,
            PresetName: "Bass Swap",
            IncomingTrack: new CueForgeTrackRef("track-2", "Second Track", "Artist B")));

        Assert.True(vm.IsMixingIncoming);
        Assert.Equal(42.0, vm.IncomingMixProgressPercent);
        Assert.Contains("Artist B — Second Track", vm.IncomingMixSummary);
        Assert.Contains("Bass Swap", vm.IncomingMixSummary);
        Assert.Contains("42%", vm.IncomingMixSummary);
        Assert.True(vm.CanOpenIncomingMixTrack);

        // Clicking "Open incoming" switches Cue Forge to Track 2.
        await vm.OpenIncomingMixTrackCommand.Execute();
        Assert.Equal("track-2", vm.TrackHash);
        Assert.False(vm.CanOpenIncomingMixTrack); // already viewing Track 2

        // Crossfade finishes -> banner hides.
        transport.IncomingMix.OnNext(new CueForgeIncomingMixInfo(false, 0, null, null));
        Assert.False(vm.IsMixingIncoming);
    }

    private sealed class MixBannerTransport : ICueForgeTransport
    {
        public readonly Subject<CueForgeIncomingMixInfo> IncomingMix = new();
        private readonly Subject<bool> _playing = new();
        private readonly Subject<(double, double)> _pos = new();
        private readonly Subject<CueForgeTrackRef?> _track = new();

        public bool IsPlaying => true;
        public string? CurrentFilePath => "track1.flac";
        public string? CurrentTrackHash => "track-1";
        public string Title => "First Track";
        public string Artist => "Artist A";
        public double PositionFraction => 0.75;
        public double LengthSeconds => 240;

        public IObservable<bool> IsPlayingChanged => _playing;
        public IObservable<(double Fraction, double LengthSeconds)> PositionChanged => _pos;
        public IObservable<CueForgeTrackRef?> CurrentTrackChanged => _track;
        public IObservable<CueForgeIncomingMixInfo> IncomingMixChanged => IncomingMix;

        public void LoadPaused(string filePath, string title, string artist) { }
        public void Play() { }
        public void Pause() { }
        public void SeekFraction(double fraction) { }
        public void SetMetronome(IReadOnlyList<double>? beatTimes, double downbeatSeconds, bool enabled) { }
        public Task<string?> ResolveFilePathAsync(string trackHash) => Task.FromResult<string?>($"{trackHash}.flac");
    }
}
