using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>Track-list preparation states stay explicit and route to existing repair commands.</summary>
public class PlaylistTrackReadinessTests
{
    [Fact]
    public void DownloadedTrackWithoutAnalysis_OffersAnalysis()
    {
        using var track = CreateTrack();

        Assert.Equal("Analyse", track.PreparationStatus);
        Assert.True(track.NeedsAnalysis);
        Assert.True(track.HasPreparationRepairAction);
        Assert.Same(track.AnalyzeTrackCommand, track.PreparationRepairCommand);
    }

    [Fact]
    public void AnalysedTrackWithoutGridAnchor_OffersCueForge()
    {
        using var track = CreateTrack();
        track.Model.BPM = 128;

        Assert.Equal("Set grid", track.PreparationStatus);
        Assert.True(track.NeedsGridRepair);
        Assert.Same(track.OpenInCueForgeCommand, track.PreparationRepairCommand);
    }

    [Fact]
    public void GriddedTrackWithoutCues_OffersCuePreparation()
    {
        using var track = CreateTrack();
        track.Model.BPM = 128;
        track.Model.AnalysisOffset = 0;

        Assert.Equal("Add cues", track.PreparationStatus);
        Assert.True(track.NeedsCuePreparation);
        Assert.Same(track.OpenInCueForgeCommand, track.PreparationRepairCommand);
    }

    [Fact]
    public void FullyPreparedTrack_IsExportReadyWithoutAnUnnecessaryRepairButton()
    {
        using var track = CreateTrack();
        track.Model.BPM = 128;
        track.Model.AnalysisOffset = 0;
        track.Model.CuePointCount = 4;

        Assert.Equal("Export ready", track.PreparationStatus);
        Assert.True(track.IsExportReady);
        Assert.False(track.HasPreparationRepairAction);
        Assert.Contains("4 saved cues", track.PreparationTooltip);
        Assert.Contains("preflight verifies", track.PreparationTooltip);
    }

    [Fact]
    public void CompletedTrackWithoutFilePath_OffersWorkstationRepair()
    {
        using var track = CreateTrack();
        track.Model.ResolvedFilePath = string.Empty;

        Assert.Equal("Locate file", track.PreparationStatus);
        Assert.True(track.NeedsFileRepair);
        Assert.Same(track.OpenWorkstationCommand, track.PreparationRepairCommand);
    }

    [Fact]
    public void MissingTrack_KeepsDownloadStateSeparateFromPreparation()
    {
        using var track = CreateTrack();
        track.Model.Status = TrackStatus.Missing;
        track.Model.AvailabilityState = TrackAvailabilityState.Ghost;
        using var missing = new PlaylistTrackViewModel(track.Model);

        Assert.Equal("Retry", missing.PreparationStatus);
        Assert.False(missing.IsCompleted);
        Assert.False(missing.NeedsAnalysis);
        Assert.Same(missing.RetryCommand, missing.PreparationRepairCommand);
    }

    private static PlaylistTrackViewModel CreateTrack() => new(new PlaylistTrack
    {
        TrackUniqueHash = "readiness-hash",
        Artist = "Artist",
        Title = "Track",
        Status = TrackStatus.Downloaded,
        AvailabilityState = TrackAvailabilityState.LocalUnanalyzed,
        ResolvedFilePath = @"C:\Music\track.flac",
    });
}
