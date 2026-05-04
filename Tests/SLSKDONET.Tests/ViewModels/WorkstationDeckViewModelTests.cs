using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Workstation;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class WorkstationDeckViewModelTests
{
    [Fact]
    public void ResolvePhraseJumpTarget_PrefersMatchingCueRole()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 12.5, Name = "Build 1", Role = CueRole.Build },
            new() { Timestamp = 44.0, Name = "Drop 1", Role = CueRole.Drop }
        };

        var target = WorkstationDeckViewModel.ResolvePhraseJumpTarget(cues, CueRole.Drop, 120, 0.55);

        Assert.Equal(44.0, target, 3);
    }

    [Fact]
    public void ResolvePhraseJumpTarget_UsesFallbackRatioWhenCueMissing()
    {
        var target = WorkstationDeckViewModel.ResolvePhraseJumpTarget(new List<OrbitCue>(), CueRole.Outro, 200, 0.82);

        Assert.Equal(164.0, target, 3);
    }

    [Fact]
    public void BuildSuggestedHotCues_PrioritizesCoreSections()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 4.0, Name = "Intro", Role = CueRole.Intro, Color = "#00FFFF", Confidence = 0.9 },
            new() { Timestamp = 31.5, Name = "Build", Role = CueRole.Build, Color = "#FFFF00", Confidence = 0.92 },
            new() { Timestamp = 48.0, Name = "Drop", Role = CueRole.Drop, Color = "#FF0000", Confidence = 0.97 },
            new() { Timestamp = 92.0, Name = "Breakdown", Role = CueRole.Breakdown, Color = "#800080", Confidence = 0.8 },
            new() { Timestamp = 180.0, Name = "Outro", Role = CueRole.Outro, Color = "#00FFFF", Confidence = 0.88 }
        };

        var hotCues = WorkstationDeckViewModel.BuildSuggestedHotCues(cues).ToList();

        Assert.Equal(5, hotCues.Count);
        Assert.Equal("Intro", hotCues[0].Label);
        Assert.Equal("Build", hotCues[1].Label);
        Assert.Equal("Drop", hotCues[2].Label);
        Assert.Equal("Breakdown", hotCues[3].Label);
        Assert.Equal("Outro", hotCues[4].Label);
    }

    [Fact]
    public void BuildSuggestedHotCues_DeduplicatesNearbyMarkers()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 48.0, Name = "Drop A", Role = CueRole.Drop, Confidence = 0.8 },
            new() { Timestamp = 49.2, Name = "Drop B", Role = CueRole.Drop, Confidence = 0.95 },
            new() { Timestamp = 120.0, Name = "Outro", Role = CueRole.Outro, Confidence = 0.8 }
        };

        var hotCues = WorkstationDeckViewModel.BuildSuggestedHotCues(cues).ToList();

        Assert.Single(hotCues.Where(h => h.Label.Contains("Drop")));
        Assert.Contains(hotCues, h => h.Label == "Outro");
    }

    [Theory]
    [InlineData("Intro", "IN")]
    [InlineData("Build 1", "BLD")]
    [InlineData("Drop", "DRP")]
    [InlineData("Outro", "OUT")]
    [InlineData("", "1")]
    public void BuildHotCuePadText_UsesFriendlyShortLabels(string label, string expected)
    {
        HotCue? cue = string.IsNullOrWhiteSpace(label)
            ? null
            : new HotCue { Slot = 0, Label = label, PositionSeconds = 12.5, Color = "#FFFFFF" };

        var text = WorkstationDeckViewModel.BuildHotCuePadText(cue, 0);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void BuildCuePrepSummary_ReportsPreparedPadCount()
    {
        var summary = WorkstationDeckViewModel.BuildCuePrepSummary(new HotCue?[]
        {
            new HotCue { Slot = 0, Label = "Intro", PositionSeconds = 0 },
            new HotCue { Slot = 1, Label = "Drop", PositionSeconds = 48 },
            null,
            null
        });

        Assert.Contains("2 prep pads", summary);
        Assert.Contains("Intro", summary);
        Assert.Contains("Drop", summary);
    }

    [Fact]
    public void BuildMixReadinessText_ReportsReadyWhenCoreCuesExist()
    {
        var text = WorkstationDeckViewModel.BuildMixReadinessText(
            "8A",
            124.0,
            new List<OrbitCue>
            {
                new() { Timestamp = 0, Role = CueRole.Intro, Name = "Intro" },
                new() { Timestamp = 48, Role = CueRole.Drop, Name = "Drop" },
                new() { Timestamp = 180, Role = CueRole.Outro, Name = "Outro" }
            });

        Assert.Contains("Mix-ready", text);
        Assert.Contains("8A", text);
        Assert.Contains("124.0 BPM", text);
    }

    [Fact]
    public void BuildSectionJumpSummary_ReturnsFallbackWhenNoCuesExist()
    {
        var text = WorkstationDeckViewModel.BuildSectionJumpSummary(Array.Empty<OrbitCue>());

        Assert.Contains("guided", text);
    }

    [Fact]
    public void BuildSectionJumpSummary_ListsPreparedAnchors()
    {
        var text = WorkstationDeckViewModel.BuildSectionJumpSummary(
            new List<OrbitCue>
            {
                new() { Timestamp = 0, Role = CueRole.Intro, Name = "Intro" },
                new() { Timestamp = 24, Role = CueRole.Build, Name = "Build" },
                new() { Timestamp = 48, Role = CueRole.Drop, Name = "Drop" },
                new() { Timestamp = 180, Role = CueRole.Outro, Name = "Outro" }
            });

        Assert.Contains("Intro", text);
        Assert.Contains("Build", text);
        Assert.Contains("Drop", text);
        Assert.Contains("Outro", text);
    }

    [Fact]
    public void BuildTransitionStatus_ReportsSmoothBlendForCompatibleKeys()
    {
        var text = WorkstationDeckViewModel.BuildTransitionStatus(
            "A",
            "8A",
            124.0,
            new List<OrbitCue> { new() { Role = CueRole.Outro, Timestamp = 180, Name = "Outro" } },
            "B",
            "9A",
            124.8,
            new List<OrbitCue> { new() { Role = CueRole.Intro, Timestamp = 0, Name = "Intro" } });

        Assert.Contains("Deck B", text);
        Assert.Contains("Smooth blend", text);
        Assert.Contains("+0.8 BPM", text);
    }

    [Fact]
    public void BuildPerformanceStatusSummary_ReportsLiveDeckState()
    {
        var text = WorkstationDeckViewModel.BuildPerformanceStatusSummary("B", true, true, true, true, 2.5);

        Assert.Contains("Deck B", text);
        Assert.Contains("Live", text);
        Assert.Contains("loop on", text);
        Assert.Contains("key lock", text);
        Assert.Contains("+2.5%", text);
    }

    [Fact]
    public void BuildWaveformDetailSummary_ReportsSemanticWindowWhenZoomedIn()
    {
        var text = WorkstationDeckViewModel.BuildWaveformDetailSummary(true, 4.0, 0.35);

        Assert.Contains("Semantic detail", text);
        Assert.Contains("4.0×", text);
        Assert.Contains("35%", text);
    }

    [Fact]
    public void BuildPerformanceStatusSummary_ReportsCueState_WhenDeckIsNotLoaded()
    {
        var text = WorkstationDeckViewModel.BuildPerformanceStatusSummary("A", false, false, false, false, 0);

        Assert.Contains("Load a track", text);
    }

    [Fact]
    public void BuildDeckStatusSummary_ReportsLoadedDecksAndFocus()
    {
        var text = WorkstationViewModel.BuildDeckStatusSummary(2, 4, "B", 124.5);

        Assert.Contains("2/4 decks live", text);
        Assert.Contains("Focus B", text);
        Assert.Contains("124.5 BPM", text);
    }

    [Fact]
    public void BuildToolbarHint_ReportsModeAndGuides()
    {
        var text = WorkstationViewModel.BuildToolbarHint(WorkstationMode.Waveform, true, true);

        Assert.Contains("Waveform", text);
        Assert.Contains("Snap on", text);
        Assert.Contains("Quantize on", text);
    }

    [Fact]
    public void BuildDeckFocusSummary_ReportsFocusedAndOpenTargets()
    {
        var text = WorkstationViewModel.BuildDeckFocusSummary(new[] { "A", "B", "C" }, new[] { "A", "B" }, "B");

        Assert.Contains("A live", text);
        Assert.Contains("B focused", text);
        Assert.Contains("C open", text);
    }

    [Fact]
    public void BuildFlowWindowSummary_ReportsViewportRange()
    {
        var text = WorkstationViewModel.BuildFlowWindowSummary(30, 60);

        Assert.Contains("0:30", text);
        Assert.Contains("1:30", text);
        Assert.Contains("60s", text);
    }

    [Fact]
    public void BuildPlaylistFlowSummary_ReportsPlaylistReadinessAndMode()
    {
        var text = WorkstationViewModel.BuildPlaylistFlowSummary("Peak Set", 12, 2, WorkstationMode.Flow);

        Assert.Contains("Peak Set", text);
        Assert.Contains("12 flow-ready tracks", text);
        Assert.Contains("2 live decks", text);
        Assert.Contains("flow active", text);
    }

    [Fact]
    public void BuildAnalysisQueueSummary_ReportsPausedPrepState()
    {
        var text = WorkstationViewModel.BuildAnalysisQueueSummary(2, 18, "track-hash", true, "Stealth", 1);

        Assert.Contains("Analysis paused", text);
        Assert.Contains("2 queued", text);
        Assert.Contains("18 prepped", text);
        Assert.Contains("Stealth", text);
    }

    [Fact]
    public void BuildTransportStatusSummary_ReportsLiveMixState()
    {
        var text = WorkstationViewModel.BuildTransportStatusSummary(true, 2, "B", true);

        Assert.Contains("Live", text);
        Assert.Contains("2 decks rolling", text);
        Assert.Contains("Focus B", text);
        Assert.Contains("loop armed", text);
    }

    [Fact]
    public void BuildFocusedDeckActionSummary_ReportsPreparedDeckActions()
    {
        var text = WorkstationViewModel.BuildFocusedDeckActionSummary("B", true, true, true);

        Assert.Contains("Deck B", text);
        Assert.Contains("jump cues", text);
        Assert.Contains("stems", text);
    }

    [Fact]
    public void BuildMixCoachSummary_CombinesHarmonicAndTransitionGuidance()
    {
        var text = WorkstationViewModel.BuildMixCoachSummary("B", "Harmonic lock • blend approved", "Smooth blend into Deck A");

        Assert.Contains("Deck B", text);
        Assert.Contains("Harmonic", text);
        Assert.Contains("Smooth blend", text);
    }

    [Fact]
    public void BuildHarmonicSuggestionText_ReportsLockForCompatibleKeys()
    {
        var text = WorkstationDeckViewModel.BuildHarmonicSuggestionText("8A", "9A", 0);

        Assert.Contains("Harmonic", text);
        Assert.Contains("blend", text);
    }

    [Fact]
    public void BuildHarmonicSuggestionText_IncludesShiftWhenDeckIsTransposed()
    {
        var text = WorkstationDeckViewModel.BuildHarmonicSuggestionText("8A", null, 2);

        Assert.Contains("+2 st", text);
    }

    [Fact]
    public void IsTrackReadyForWorkstation_RequiresLocalFileAndPrepData()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var missingPrep = new PlaylistTrack
            {
                Status = TrackStatus.Downloaded,
                ResolvedFilePath = tempFile,
                Artist = "Artist",
                Title = "Unprepared"
            };

            var ready = new PlaylistTrack
            {
                Status = TrackStatus.Downloaded,
                ResolvedFilePath = tempFile,
                Artist = "Artist",
                Title = "Prepared",
                BPM = 128,
                WaveformData = new byte[] { 1, 2, 3 }
            };

            Assert.False(WorkstationDeckViewModel.IsTrackReadyForWorkstation(missingPrep));
            Assert.True(WorkstationDeckViewModel.IsTrackReadyForWorkstation(ready));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LoadPlaylistTrackCommand_DoesNotThrow_WhenFileIsMissing()
    {
        var separatorInner = new Mock<IStemSeparator>();
        separatorInner.SetupGet(x => x.Name).Returns("stub");
        separatorInner.SetupGet(x => x.IsAvailable).Returns(false);
        separatorInner.SetupGet(x => x.ModelTag).Returns("stub-model");

        var separator = new CachedStemSeparator(
            separatorInner.Object,
            new StemCacheService(new NullLogger<StemCacheService>()),
            new NullLogger<CachedStemSeparator>());

        var vm = new WorkstationDeckViewModel(
            "A",
            new DeckSlotViewModel("A", new DeckEngine()),
            separator,
            Mock.Of<ICuePointService>(),
            null!);

        var track = new PlaylistTrack
        {
            Status = TrackStatus.Downloaded,
            Artist = "Missing Artist",
            Title = "Missing Track",
            ResolvedFilePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp3"),
            BPM = 124,
            WaveformData = new byte[] { 1 }
        };

        var exception = await Record.ExceptionAsync(async () =>
            await vm.LoadPlaylistTrackCommand.Execute(track).FirstAsync());

        Assert.Null(exception);
        Assert.False(vm.IsLoaded);
        Assert.False(string.IsNullOrWhiteSpace(vm.TrackLoadError));
    }
}
