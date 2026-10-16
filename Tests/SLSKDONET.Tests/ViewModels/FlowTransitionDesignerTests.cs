using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Threading.Tasks;
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

/// <summary>Flow Builder as a transition designer: preview timeline mapping, save gating,
/// pair scoring display, and the saved/dirty state of the transition editor.</summary>
public class FlowTransitionDesignerTests
{
    // ── Preview timeline: [lead-in A][window A+B][tail B] ──────────────────────────────

    [Fact]
    public void PreviewPlayhead_MapsLeadInWindowAndTail()
    {
        // lead-in 8 s, mix-out at 200 s, mix-in at 16 s, 30 s window
        Assert.Equal(192, MixTransitionViewModel.MapPreviewToOutgoing(0, 8, 200, 30));
        Assert.Equal(200, MixTransitionViewModel.MapPreviewToOutgoing(8, 8, 200, 30));
        Assert.Null(MixTransitionViewModel.MapPreviewToOutgoing(40, 8, 200, 30)); // tail: A is gone

        Assert.Null(MixTransitionViewModel.MapPreviewToIncoming(4, 8, 16));       // B not in yet
        Assert.Equal(16, MixTransitionViewModel.MapPreviewToIncoming(8, 8, 16));
        Assert.Equal(46, MixTransitionViewModel.MapPreviewToIncoming(38, 8, 16));
    }

    [Theory]
    [InlineData(120, 0.5)]
    [InlineData(0, 0.25)]
    public void BeatSeconds_UsesTempoWithFallback(double bpm, double expected)
        => Assert.Equal(expected, MixTransitionViewModel.BeatSeconds(bpm), 6);

    [Fact]
    public void EveryPreset_HasADescription()
    {
        foreach (var preset in MixTransitionViewModel.PresetNames)
            Assert.False(string.IsNullOrWhiteSpace(MixTransitionViewModel.DescribePreset(preset)), preset);
    }

    // ── Save gating ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SaveIsBlocked_WhenATrackIsNotInThePlaylistYet()
    {
        var playlist = Guid.NewGuid();
        var a = Track(playlist, "A");
        var bridge = Track(Guid.NewGuid(), "Bridge");

        Assert.Null(FlowBuilderViewModel.GetSaveBlockedReason(playlist, a, Track(playlist, "B")));
        Assert.Contains("Bridge", FlowBuilderViewModel.GetSaveBlockedReason(playlist, a, bridge));
        Assert.NotNull(FlowBuilderViewModel.GetSaveBlockedReason(null, a, a));
    }

    [Fact]
    public void IsPlayable_KeepsTracksThatOnlyNeedAnalysis()
    {
        var file = System.IO.Path.GetTempFileName();
        try
        {
            var needsAnalysis = Track(Guid.NewGuid(), "A");
            needsAnalysis.Status = TrackStatus.Downloaded;
            needsAnalysis.ResolvedFilePath = file;
            needsAnalysis.TrackUniqueHash = "hash-a";
            Assert.True(FlowBuilderViewModel.IsPlayable(needsAnalysis));

            var notDownloaded = Track(Guid.NewGuid(), "B");
            notDownloaded.TrackUniqueHash = "hash-b";
            Assert.False(FlowBuilderViewModel.IsPlayable(notDownloaded));
        }
        finally { System.IO.File.Delete(file); }
    }

    // ── Pair link ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Bridge_ShowsScoreAsMeterTierAndKeyRelation()
    {
        var a = Card("8A", 126);
        var b = Card("9A", 128);
        a.SetBridgeTo(b);

        Assert.NotNull(a.Bridge);
        Assert.InRange(a.Bridge!.ScorePercent, 0, 100);
        Assert.Equal(1, new[] { a.Bridge.IsGoodScore, a.Bridge.IsFairScore, a.Bridge.IsPoorScore }.Count(x => x));
        Assert.Equal("8A → 9A", a.Bridge.KeyRelationDisplay);
        Assert.False(a.PairNeedsAnalysis); // not downloaded is a different issue than "needs analysis"

        a.SetBridgeTo(null);
        Assert.False(a.HasBridge);
    }

    [Fact]
    public void RegionOverlay_UsesWaveformZoomMapping()
    {
        Assert.Equal(50, TimeRegionOverlay.TimeToX(50, 100, 1, 0, 100), 6);
        Assert.Equal(100, TimeRegionOverlay.TimeToX(50, 100, 4, 0.25, 100), 6);
        Assert.Equal(0, TimeRegionOverlay.TimeToX(10, 0, 1, 0, 100));
    }

    // ── Saved / dirty state ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Editor_TracksDirtyAndSavedState_AndSaveFailureIsNotReportedAsSaved()
    {
        var repo = new Mock<ITransitionRepository>();
        repo.Setup(r => r.GetTransitionAsync(It.IsAny<Guid>(), It.IsAny<Guid>())).ReturnsAsync((PlaylistTrackTransition?)null);
        var vm = CreateEditor(repo);
        var playlist = Guid.NewGuid();

        await vm.LoadPairAsync(playlist, Track(playlist, "A"), Track(playlist, "B"));
        Assert.True(vm.HasLoadedPair);
        Assert.False(vm.IsDirty);
        Assert.True(vm.IsAutoPlan);

        vm.DurationBars = vm.DurationBars == 8 ? 16 : 8;
        Assert.True(vm.IsDirty);
        Assert.Equal("Unsaved changes", vm.SaveStateText);

        repo.Setup(r => r.UpsertTransitionAsync(It.IsAny<PlaylistTrackTransition>())).ThrowsAsync(new InvalidOperationException("db locked"));
        await vm.SaveCommand.Execute().ToTask();
        Assert.True(vm.IsDirty);
        Assert.False(vm.HasSavedTransition);

        repo.Setup(r => r.UpsertTransitionAsync(It.IsAny<PlaylistTrackTransition>())).Returns(Task.CompletedTask);
        var raised = false;
        vm.SavedStateChanged += (_, _) => raised = true;
        await vm.SaveCommand.Execute().ToTask();
        Assert.False(vm.IsDirty);
        Assert.True(vm.IsSavedAndClean);
        Assert.True(raised);
    }

    [Fact]
    public async Task Editor_BarsSegmentFollowsDurationBars()
    {
        var repo = new Mock<ITransitionRepository>();
        var vm = CreateEditor(repo);
        var playlist = Guid.NewGuid();
        await vm.LoadPairAsync(playlist, Track(playlist, "A"), Track(playlist, "B"));

        vm.IsBars32 = true;
        Assert.Equal(32, vm.DurationBars);
        Assert.True(vm.IsBars32);
        Assert.False(vm.IsBars8);
    }

    [Fact]
    public void SaveCommand_IsDisabledWhileBlocked()
    {
        var vm = CreateEditor(new Mock<ITransitionRepository>());
        bool? canSave = null;
        using var sub = vm.SaveCommand.CanExecute.Subscribe(v => canSave = v);
        vm.SaveBlockedReason = "Save the order first";
        Assert.False(canSave);
        Assert.True(vm.IsSaveBlocked);
        vm.SaveBlockedReason = null;
        Assert.True(canSave);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static PlaylistTrack Track(Guid playlistId, string title) => new()
    {
        Id = Guid.NewGuid(),
        PlaylistId = playlistId,
        Title = title,
        Artist = "Artist",
        TrackUniqueHash = "hash-" + title,
        BPM = 128,
    };

    private static FlowTrackCardViewModel Card(string key, double bpm)
    {
        var t = Track(Guid.NewGuid(), key);
        t.MusicalKey = key;
        t.BPM = bpm;
        return new FlowTrackCardViewModel(t, () => { }, () => { }, () => { });
    }

    private static MixTransitionViewModel CreateEditor(Mock<ITransitionRepository> repo)
    {
        var library = new Mock<ILibraryService>();
        var cues = new Mock<ICuePointService>();
        cues.Setup(c => c.GetByTrackIdAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(new List<CuePointEntity>());
        var services = new Mock<IServiceProvider>();
        return new MixTransitionViewModel(
            NullLogger<MixTransitionViewModel>.Instance,
            library.Object,
            new EventBusService(),
            null!,
            repo.Object,
            new Mock<ITransitionPreviewPlayer>().Object,
            new Mock<SLSKDONET.Services.Repositories.ITrackRepository>().Object,
            cues.Object,
            services.Object);
    }
}
