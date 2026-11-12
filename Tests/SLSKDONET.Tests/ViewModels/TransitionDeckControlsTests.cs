using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Repositories;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Flow Builder transition editor deck controls: per-deck zoom, the draggable mix markers (snapped
/// to a cue or the beat), and the cue rail that lists every cue regardless of the waveform filter.
/// </summary>
public class TransitionDeckControlsTests
{
    [Fact]
    public void SnapMixPoint_KeepsAPointThatSitsOnACue()
    {
        var cues = new[] { new OrbitCue { Timestamp = 61.37, Name = "Break" } };
        Assert.Equal(61.37, MixTransitionViewModel.SnapMixPoint(61.37, cues, bpm: 174, gridOffset: 0.2, duration: 300));
    }

    [Fact]
    public void SnapMixPoint_OffACue_GoesToTheNearestBeatOfTheGrid()
    {
        // 120 BPM = 0.5 s beats from a downbeat at 0.1 s: (10.33 - 0.1) / 0.5 = 20.46 → beat 20 = 10.1 s.
        double snapped = MixTransitionViewModel.SnapMixPoint(10.33, Array.Empty<OrbitCue>(), bpm: 120, gridOffset: 0.1, duration: 300);
        Assert.Equal(10.1, snapped, 6);
    }

    [Fact]
    public void SnapMixPoint_WithoutTempo_LeavesThePoint_ButKeepsItInsideTheTrack()
    {
        Assert.Equal(42.123, MixTransitionViewModel.SnapMixPoint(42.123, Array.Empty<OrbitCue>(), bpm: 0, gridOffset: 0, duration: 300));
        Assert.Equal(300, MixTransitionViewModel.SnapMixPoint(312, Array.Empty<OrbitCue>(), bpm: 0, gridOffset: 0, duration: 300));
        Assert.Equal(0, MixTransitionViewModel.SnapMixPoint(-3, Array.Empty<OrbitCue>(), bpm: 0, gridOffset: 0, duration: 300));
    }

    [Fact]
    public async Task DeckZoom_IsPerDeck_AndTheSharedZoomSetsBoth()
    {
        var vm = await LoadedEditorAsync();

        vm.WaveformZoomLevel = 2;
        Assert.Equal(2, vm.OutgoingZoomLevel);
        Assert.Equal(2, vm.IncomingZoomLevel);

        vm.ZoomIncomingInCommand.Execute().Subscribe();
        Assert.Equal(2, vm.OutgoingZoomLevel);
        Assert.Equal(3, vm.IncomingZoomLevel, 6);

        vm.OutgoingZoomLevel = 99;
        Assert.Equal(16, vm.OutgoingZoomLevel);
        Assert.Equal(3, vm.IncomingZoomLevel, 6);
    }

    [Fact]
    public async Task CenterOnMixPoint_DropsAManualPan()
    {
        var vm = await LoadedEditorAsync();
        vm.OutgoingZoomLevel = 8;
        double centered = vm.OutgoingViewOffset;
        vm.OutgoingViewOffset = 0;
        Assert.Equal(0, vm.OutgoingViewOffset);

        vm.CenterOutgoingOnMixPointCommand.Execute().Subscribe();

        Assert.Equal(centered, vm.OutgoingViewOffset, 9);
    }

    [Fact]
    public async Task CueRail_ListsEveryCue_EvenWhileTheWaveformShowsMixCuesOnly()
    {
        var vm = await LoadedEditorAsync(cueRows: new()
        {
            new() { TrackUniqueHash = "hash-A", TimestampInSeconds = 30, Type = CuePointType.MixOut, Label = "Mix-Out", SlotIndex = -1 },
            new() { TrackUniqueHash = "hash-A", TimestampInSeconds = 90, Type = CuePointType.Breakdown, Label = "Break", SlotIndex = 2 },
        });

        Assert.True(vm.MixCuesOnly);
        Assert.Single(vm.OutgoingCues);
        Assert.Equal(new[] { "Mix-Out", "Break" }, vm.OutgoingCueRail.Select(c => c.Name));
        Assert.Equal(2, vm.OutgoingPickableCues.Count);
        Assert.Equal("Hot C", vm.OutgoingCueRail[1].KindDisplay);
        Assert.True(vm.OutgoingHasCues);
        Assert.False(vm.IncomingHasCues);
    }

    [Fact]
    public async Task CueRail_MixHere_PutsTheMixOutOnThatCue_HighlightsIt_AndIsUndoable()
    {
        var vm = await LoadedEditorAsync(cueRows: new()
        {
            new() { TrackUniqueHash = "hash-A", TimestampInSeconds = 90, Type = CuePointType.Breakdown, Label = "Break", SlotIndex = 2 },
        });
        double before = vm.EffectiveSourceTriggerSeconds;
        var breakCue = vm.OutgoingCueRail.Single();

        breakCue.MixHereCommand.Execute().Subscribe();

        Assert.Equal(90, vm.EffectiveSourceTriggerSeconds, 6);
        Assert.True(breakCue.IsCurrentMixPoint);
        Assert.Same(breakCue.Cue, vm.OutgoingMixCue);

        vm.UndoMixPointCommand.Execute().Subscribe();
        Assert.Equal(before, vm.EffectiveSourceTriggerSeconds, 6);
        Assert.Equal(Math.Abs(before - 90) < 0.05, vm.OutgoingCueRail.Single().IsCurrentMixPoint);
    }

    [Fact]
    public async Task DraggingTheMixInMarker_SnapsToTheBeat_AndIsUndoable()
    {
        var vm = await LoadedEditorAsync();
        double before = vm.TargetTriggerSeconds;
        var session = vm.IncomingEditor.Session;
        double bpm = session.Bpm > 0 ? session.Bpm : vm.IncomingBpm;
        double beat = 60.0 / bpm;

        vm.SetMixInAtCommand.Execute(47.31).Subscribe();

        double beats = (vm.TargetTriggerSeconds - session.GridOffset) / beat;
        Assert.Equal(Math.Round(beats), beats, 6);
        Assert.True(Math.Abs(vm.TargetTriggerSeconds - 47.31) <= beat / 2 + 1e-9);
        Assert.Contains("on the beat", vm.StatusMessage);

        vm.UndoMixPointCommand.Execute().Subscribe();
        Assert.Equal(before, vm.TargetTriggerSeconds, 6);
    }

    [Fact]
    public async Task DroppingTheMixOutMarkerOnACue_PutsItExactlyThere()
    {
        var vm = await LoadedEditorAsync(cueRows: new()
        {
            new() { TrackUniqueHash = "hash-A", TimestampInSeconds = 61.37, Type = CuePointType.Breakdown, Label = "Break", SlotIndex = 1 },
        });

        vm.SetMixOutAtCommand.Execute(61.37).Subscribe();

        Assert.Equal(61.37, vm.EffectiveSourceTriggerSeconds, 6);
        Assert.Contains("on Break", vm.StatusMessage);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static async Task<MixTransitionViewModel> LoadedEditorAsync(List<CuePointEntity>? cueRows = null)
    {
        var repo = new Mock<ITransitionRepository>();
        repo.Setup(r => r.GetTransitionAsync(It.IsAny<Guid>(), It.IsAny<Guid>())).ReturnsAsync((PlaylistTrackTransition?)null);
        var library = new Mock<ILibraryService>();
        var cues = new Mock<ICuePointService>();
        cues.Setup(c => c.GetByTrackIdAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((string hash, System.Threading.CancellationToken _) => (cueRows ?? new()).Where(r => r.TrackUniqueHash == hash).ToList());
        var vm = new MixTransitionViewModel(
            NullLogger<MixTransitionViewModel>.Instance,
            library.Object,
            new EventBusService(),
            null!,
            repo.Object,
            new Mock<ITransitionPreviewPlayer>().Object,
            new Mock<ITrackRepository>().Object,
            cues.Object,
            new Mock<IServiceProvider>().Object,
            new Mock<ILibraryPreviewPlayer>().Object);
        var playlist = Guid.NewGuid();
        await vm.LoadPairAsync(playlist, Track(playlist, "A"), Track(playlist, "B"));
        return vm;
    }

    private static PlaylistTrack Track(Guid playlistId, string title) => new()
    {
        Id = Guid.NewGuid(),
        PlaylistId = playlistId,
        Title = title,
        Artist = "Artist",
        TrackUniqueHash = "hash-" + title,
        BPM = 128,
        CanonicalDuration = 300_000,
        ResolvedFilePath = "C:/music/" + title + ".flac",
    };
}
