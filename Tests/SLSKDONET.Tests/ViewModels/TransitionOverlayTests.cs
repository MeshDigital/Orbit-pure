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
using SLSKDONET.Views.Avalonia.Controls;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>The transition editor's overlay view: alignment and tempo-correction maths, the blend
/// window for each Length, live recompute when the mix points / length / preset change, and the
/// drag-to-shift-B result (snapped, undoable).</summary>
public class TransitionOverlayTests
{
    private static TransitionOverlayModel Model(double outBpm = 174, double inBpm = 170, double source = 200, double target = 16,
        double window = 30, double context = 10, double inDownbeat = 0) =>
        TransitionOverlayModel.Build(outBpm, inBpm, source, target, window, context, 300, 300, inDownbeat, null, null, null);

    [Fact]
    public void Alignment_BothMixPointsMeetAtZero_AndBIsTempoCorrectedToTheProjectBpm()
    {
        var m = Model();

        Assert.Equal(174, m.ProjectBpm);                         // A sets the tempo
        Assert.Equal(1.0, m.OutgoingRate, 9);
        Assert.Equal(174.0 / 170.0, m.IncomingRate, 9);          // B plays 2.35 % faster

        Assert.Equal(200, m.OutgoingTrackTime(0), 9);            // mix-out point at shared 0…
        Assert.Equal(16, m.IncomingTrackTime(0), 9);             // …is where B comes in
        Assert.Equal(16 + 10 * 174.0 / 170.0, m.IncomingTrackTime(10), 9);
        Assert.Equal(190, m.OutgoingTrackTime(-10), 9);

        Assert.True(m.OutgoingSoundsAt(-10) && m.OutgoingSoundsAt(30));
        Assert.False(m.OutgoingSoundsAt(31));                    // A is gone after the window
        Assert.False(m.IncomingSoundsAt(-1));                    // B isn't in before the mix-out
        Assert.Equal((0.0, 30.0), m.Overlap);
        Assert.Equal(-10, m.StartTime, 9);
        Assert.Equal(40, m.EndTime, 9);
    }

    [Fact]
    public void GainOutsideTheWindow_IsFullForTheTrackThatPlays()
    {
        var m = TransitionOverlayModel.Build(128, 128, 100, 0, 30, 8, 300, 300, 0,
            new float[] { 1f, 0.5f, 0f }, new float[] { 0f, 0.5f, 1f }, null);
        Assert.Equal(1, m.OutgoingGainAt(-1), 6);
        Assert.Equal(0, m.OutgoingGainAt(31), 6);
        Assert.Equal(0.5, m.OutgoingGainAt(15), 6);
        Assert.Equal(0, m.IncomingGainAt(-1), 6);
        Assert.Equal(1, m.IncomingGainAt(31), 6);
        Assert.Equal(0.75, m.IncomingGainAt(22.5), 6);
    }

    [Fact]
    public void BarLabels_CountTheBlendFromOne()
    {
        Assert.Equal(1, TransitionOverlayModel.BarLabel(0));
        Assert.Equal(16, TransitionOverlayModel.BarLabel(15));
        Assert.Equal(-1, TransitionOverlayModel.BarLabel(-1));
    }

    [Fact]
    public void XMapping_CoversTheContextAndTheWindow()
    {
        var m = Model(window: 30, context: 10);                  // -10 … 40 s on 500 px
        Assert.Equal(0, TransitionOverlayControl.TimeToX(-10, m, 500), 6);
        Assert.Equal(100, TransitionOverlayControl.TimeToX(0, m, 500), 6);
        Assert.Equal(500, TransitionOverlayControl.TimeToX(40, m, 500), 6);
        Assert.Equal(0, TransitionOverlayControl.XToTime(100, m, 500), 6);
    }

    [Fact]
    public void DraggingB_SnapsTheMixInToBsOwnBars_OrBeatsWithShift()
    {
        // B at 128 BPM (bar 1.875 s) with its downbeat at 0.2 s, coming in at 30.2 s.
        var m = Model(outBpm: 128, inBpm: 128, target: 30.2, inDownbeat: 0.2);

        // Drag B one second to the right: its content moves later, so it comes in from earlier in B.
        Assert.Equal(28.325, m.ShiftedTargetTrigger(1.0), 6);                   // nearest bar of B
        Assert.Equal(29.2625, m.ShiftedTargetTrigger(1.0, snapToBar: false), 6); // nearest beat: 29.2 → 29.2625
        Assert.True(m.ShiftedTargetTrigger(1000) >= 0);                          // never before B's start

        // The drawn offset of the snapped ghost follows the tempo correction.
        var fast = Model(outBpm: 174, inBpm: 170, target: 40);
        Assert.Equal((40 - 38) / (174.0 / 170.0), fast.DrawOffsetFor(38), 9);
    }

    // ── Live in the editor ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public async Task OverlapWindow_FollowsTheLength(int bars)
    {
        var vm = await LoadedEditorAsync();
        vm.DurationBars = bars;

        var o = vm.Overlay!;
        Assert.Equal(bars * 4 * 60.0 / 128, o.WindowSeconds, 6);
        Assert.Equal(bars, o.WindowBars);
        Assert.Equal(o.WindowSeconds, o.Overlap.End, 6);
        Assert.Equal(vm.PreviewLeadInSeconds, o.PreSeconds, 6); // same layout as Preview
    }

    [Fact]
    public async Task Overlay_RecomputesLive_OnNudgesMixPointsAndPreset()
    {
        var vm = await LoadedEditorAsync();
        var before = vm.Overlay!;

        vm.NudgeForwardCommand.Execute().Subscribe();
        Assert.Equal(before.SourceTrigger + 60.0 / 128, vm.Overlay!.SourceTrigger, 6);

        vm.NudgeIncomingForwardCommand.Execute().Subscribe();
        Assert.Equal(before.TargetTrigger + 60.0 / 128, vm.Overlay!.TargetTrigger, 6);

        vm.SelectedPreset = "Fade";
        Assert.Empty(vm.Overlay!.Markers);
        vm.SelectedPreset = "Rolling";                           // hard bass swap halfway through
        var swap = Assert.Single(vm.Overlay!.Markers);
        Assert.Equal("Bass swap", swap.Label);
        Assert.Equal(vm.Overlay.WindowSeconds / 2, swap.Seconds, 6);
        Assert.NotEmpty(vm.Overlay.OutgoingGain);

        vm.IncomingBpm = 132;                                    // tempo correction follows a BPM fix
        Assert.Equal(128.0 / 132.0, vm.Overlay!.IncomingRate, 9);
    }

    [Fact]
    public async Task OverlayDrag_WritesTheMixIn_AndIsUndoable()
    {
        var vm = await LoadedEditorAsync();
        double original = vm.TargetTriggerSeconds;

        vm.SetMixInFromOverlayCommand.Execute(original + 7.5).Subscribe();
        Assert.Equal(original + 7.5, vm.TargetTriggerSeconds, 6);
        Assert.Equal(original + 7.5, vm.Overlay!.TargetTrigger, 6);
        Assert.True(vm.CanUndoMixPoint);
        Assert.True(vm.IsDirty);

        vm.UndoMixPointCommand.Execute().Subscribe();
        Assert.Equal(original, vm.TargetTriggerSeconds, 6);
        Assert.False(vm.CanUndoMixPoint);
    }

    [Fact]
    public async Task ClickingACueChip_OutsideEditMode_SetsTheMixPoint_AndPlaysFromTheCue()
    {
        var preview = new Mock<ILibraryPreviewPlayer>();
        var cueRows = new List<CuePointEntity>
        {
            new() { TrackUniqueHash = "hash-B", TimestampInSeconds = 48, Type = CuePointType.Drop, Label = "[DROP 1]", SlotIndex = 2 },
        };
        var vm = await LoadedEditorAsync(preview.Object, cueRows);
        vm.MixCuesOnly = false;

        var drop = vm.IncomingCues.Single(c => c.Name == "[DROP 1]");
        vm.IncomingCueChipCommand.Execute(drop).Subscribe();

        Assert.Equal(48, vm.TargetTriggerSeconds, 6);
        Assert.Same(drop, vm.IncomingEditor.Session.SelectedCue);
        preview.Verify(p => p.RequestPreview("C:/music/B.flac", It.IsAny<double?>(), 48), Times.Once);
        Assert.True(vm.CanUndoMixPoint);
    }

    [Fact]
    public async Task TempoCorrection_UsesTheLivePlaybackRateInOverlayRegionsAndPlayheadMapping()
    {
        var vm = await LoadedEditorAsync();
        vm.IncomingBpm = 132;

        double expectedRate = 128.0 / 132.0;
        Assert.Equal(expectedRate, vm.IncomingPlaybackRate, 9);
        Assert.Equal(expectedRate, vm.Overlay!.IncomingRate, 9);
        Assert.Equal(vm.TargetTriggerSeconds + vm.TransitionWindowSeconds * expectedRate,
            vm.IncomingRegionEndSeconds, 9);
        Assert.Equal(vm.TargetTriggerSeconds + 10 * expectedRate,
            MixTransitionViewModel.MapPreviewToIncoming(8 + 10, 8, vm.TargetTriggerSeconds, expectedRate)!.Value, 9);
        Assert.False(vm.HasTempoWarning);
    }

    [Fact]
    public async Task TempoCorrection_OutsideLiveRange_KeepsIncomingAudioNativeAndWarns()
    {
        var vm = await LoadedEditorAsync();
        vm.IncomingBpm = 160;

        Assert.Equal(1.0, vm.IncomingPlaybackRate, 9);
        Assert.Equal(1.0, vm.Overlay!.IncomingRate, 9);
        Assert.Equal(vm.TargetTriggerSeconds + vm.TransitionWindowSeconds, vm.IncomingRegionEndSeconds, 9);
        Assert.True(vm.HasTempoWarning);
        Assert.Contains("outside the ±6% sync range", vm.TempoSyncDisplay);
    }

    [Fact]
    public async Task FocusedCueView_HidesWorkstationCuesWithoutDeletingThem()
    {
        var cueRows = new List<CuePointEntity>
        {
            new() { TrackUniqueHash = "hash-B", TimestampInSeconds = 12, Type = CuePointType.MixIn, Label = "Mix-In", SlotIndex = -1 },
            new() { TrackUniqueHash = "hash-B", TimestampInSeconds = 48, Type = CuePointType.Drop, Label = "[DROP 1]", SlotIndex = 2 },
        };
        var vm = await LoadedEditorAsync(cueRows: cueRows);

        Assert.Equal(CueRole.MixIn, Assert.Single(vm.IncomingCues).Role);
        Assert.Equal(2, vm.IncomingEditor.Session.Cues.Count);

        vm.MixCuesOnly = false;
        Assert.Equal(2, vm.IncomingCues.Count());

        vm.MixCuesOnly = true;
        vm.IsCueEditMode = true;
        Assert.Equal(2, vm.IncomingCues.Count());

        vm.IsCueEditMode = false;
        Assert.Equal(CueRole.MixIn, Assert.Single(vm.IncomingCues).Role);
        Assert.Equal(2, vm.IncomingEditor.Session.Cues.Count);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static async Task<MixTransitionViewModel> LoadedEditorAsync(ILibraryPreviewPlayer? preview = null, List<CuePointEntity>? cueRows = null)
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
            preview);
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
