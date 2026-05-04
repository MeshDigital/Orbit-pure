using System;
using System.Collections.Generic;
using System.IO;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class PlayerViewModelUiTests
{
    [Fact]
    public void BuildTrackContextSummary_ReturnsFriendlyFallback_WhenNoTrackLoaded()
    {
        var summary = PlayerViewModel.BuildTrackContextSummary(null);

        Assert.Contains("Load a track", summary);
    }

    [Fact]
    public void BuildTrackContextSummary_IncludesBpmKeyAndGenre_WhenAvailable()
    {
        var vm = new PlaylistTrackViewModel(new PlaylistTrack
        {
            Artist = "Artist",
            Title = "Track",
            BPM = 124,
            MusicalKey = "Gm",
            PrimaryGenre = "Melodic House",
            Status = TrackStatus.Downloaded,
            ResolvedFilePath = "C:/music/test.mp3"
        });

        var summary = PlayerViewModel.BuildTrackContextSummary(vm);

        Assert.Contains("124 BPM", summary);
        Assert.Contains("6A", summary);
        Assert.Contains("Melodic House", summary);
    }

    [Fact]
    public void BuildTrackWorkflowHint_ReturnsQueuePrompt_WhenNoTrackLoaded()
    {
        var hint = PlayerViewModel.BuildTrackWorkflowHint(null);

        Assert.Contains("Queue a track", hint);
    }

    [Fact]
    public void BuildTrackWorkflowHint_IncludesEnergyAndAnalysisState_WhenPreparedTrackExists()
    {
        var vm = new PlaylistTrackViewModel(new PlaylistTrack
        {
            Artist = "Artist",
            Title = "Track",
            BPM = 124,
            MusicalKey = "Gm",
            PrimaryGenre = "Melodic House",
            Energy = 0.72,
            Status = TrackStatus.Downloaded,
            ResolvedFilePath = "C:/music/test.mp3"
        });

        var hint = PlayerViewModel.BuildTrackWorkflowHint(vm);

        Assert.Contains("Analysis ready", hint);
        Assert.Contains("Energy 7/10", hint);
    }

    [Fact]
    public void BuildPhraseJumpSummary_ReturnsFriendlyFallback_WhenNoCuesExist()
    {
        var summary = PlayerViewModel.BuildPhraseJumpSummary(null);

        Assert.Contains("guided", summary);
    }

    [Fact]
    public void BuildPhraseJumpSummary_ListsPreparedSections_WhenCuesExist()
    {
        var cues = new List<OrbitCue>
        {
            new() { Name = "Intro", Role = CueRole.Intro, Timestamp = 0 },
            new() { Name = "Build", Role = CueRole.Build, Timestamp = 32 },
            new() { Name = "Drop", Role = CueRole.Drop, Timestamp = 64 },
            new() { Name = "Outro", Role = CueRole.Outro, Timestamp = 180 }
        };

        var summary = PlayerViewModel.BuildPhraseJumpSummary(cues);

        Assert.Contains("Intro", summary);
        Assert.Contains("Build", summary);
        Assert.Contains("Drop", summary);
        Assert.Contains("Outro", summary);
    }

    [Fact]
    public void BuildWorkstationPrepSummary_ReturnsPrepPrompt_WhenNoTrackLoaded()
    {
        var summary = PlayerViewModel.BuildWorkstationPrepSummary(null);

        Assert.Contains("Prep a track", summary);
    }

    [Fact]
    public void BuildRoutingSummary_ReportsFlowAndStemReadiness_ForCompletedTrack()
    {
        var vm = new PlaylistTrackViewModel(new PlaylistTrack
        {
            Artist = "Artist",
            Title = "Track",
            BPM = 124,
            Energy = 0.72,
            Status = TrackStatus.Downloaded,
            ResolvedFilePath = "C:/music/test.mp3"
        });

        var summary = PlayerViewModel.BuildRoutingSummary(vm);

        Assert.Contains("Flow launch ready", summary);
        Assert.Contains("mix project", summary);
        Assert.Contains("handoff", summary);
    }

    [Fact]
    public void BuildTransitionPlanSummary_ReportsAnchoredMixPlan_WhenTrackIsPrepared()
    {
        var vm = new PlaylistTrackViewModel(new PlaylistTrack
        {
            Artist = "Artist",
            Title = "Track",
            BPM = 124,
            Energy = 0.72,
            Status = TrackStatus.Downloaded,
            ResolvedFilePath = "C:/music/test.mp3",
            CuePointsJson = "[{\"Timestamp\":0,\"Name\":\"Intro\",\"Role\":0}]"
        });

        var summary = PlayerViewModel.BuildTransitionPlanSummary(vm);

        Assert.Contains("intro in", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outro", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("energy", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAnalysisLaneSummary_ReportsRollingPrepState()
    {
        var summary = PlayerViewModel.BuildAnalysisLaneSummary(3, 12, "track-hash", false, "Stealth", 2);

        Assert.Contains("Analysis rolling", summary);
        Assert.Contains("3 queued", summary);
        Assert.Contains("12 prepped", summary);
        Assert.Contains("Stealth", summary);
    }

    [Fact]
    public void BuildWorkstationPrepSummary_ReportsReadyState_WhenTrackHasAnalysisAndCues()
    {
        var vm = new PlaylistTrackViewModel(new PlaylistTrack
        {
            Artist = "Artist",
            Title = "Track",
            BPM = 124,
            MusicalKey = "Gm",
            Energy = 0.72,
            Status = TrackStatus.Downloaded,
            ResolvedFilePath = "C:/music/test.mp3",
            CuePointsJson = "[{\"Timestamp\":0,\"Name\":\"Intro\",\"Role\":0}]"
        });

        var summary = PlayerViewModel.BuildWorkstationPrepSummary(vm);

        Assert.Contains("Workstation ready", summary);
        Assert.Contains("cue jump", summary);
    }

    [Fact]
    public void BuildWorkstationPrepSummary_ReportsStemRackReady_WhenTrackHasStems()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"orbit-stems-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var trackPath = Path.Combine(tempDir, "track.mp3");
            File.WriteAllText(trackPath, "stub");

            var stemDir = Path.Combine(tempDir, "Stems", "track");
            Directory.CreateDirectory(stemDir);
            File.WriteAllText(Path.Combine(stemDir, "vocals.wav"), "stub");

            var vm = new PlaylistTrackViewModel(new PlaylistTrack
            {
                Artist = "Artist",
                Title = "Track",
                BPM = 124,
                Status = TrackStatus.Downloaded,
                ResolvedFilePath = trackPath,
                CuePointsJson = "[{\"Timestamp\":0,\"Name\":\"Intro\",\"Role\":0}]"
            });

            var summary = PlayerViewModel.BuildWorkstationPrepSummary(vm);

            Assert.Contains("stem rack ready", summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
