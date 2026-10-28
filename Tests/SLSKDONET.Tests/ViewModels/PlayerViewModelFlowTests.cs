using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Models;
using SLSKDONET.Models.Entertainment;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Entertainment;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// The player-side half of Flow Mode: the VM must turn the queue into candidates, publish a plan the
/// Now Playing page can bind to, and never require the user to change track before anything appears.
/// (The engine maths and the XAML wiring have their own tests; this covers the seam between them.)
/// </summary>
public class PlayerViewModelFlowTests
{
    private static PlayerViewModel CreateSut(out FlowModeService service, params PlaylistTrackViewModel[] queue)
    {
        service = new FlowModeService(NullLogger<FlowModeService>.Instance);

        // The real constructor needs the whole service graph; these tests only exercise the Flow
        // seam, so build a bare instance and give it exactly the fields that path touches.
        var sut = (PlayerViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PlayerViewModel));
        SetField(sut, "_flowModeService", service);
        SetField(sut, "_flowModeState", new FlowModeState());
        SetField(sut, "_flowRecentlyPlayed", new LinkedList<string>());
        SetField(sut, "<Queue>k__BackingField", new ObservableCollection<PlaylistTrackViewModel>());

        foreach (var track in queue) sut.Queue.Add(track);

        return sut;
    }

    private static PlaylistTrackViewModel Track(
        string hash,
        string key = "8A",
        double bpm = 128,
        double energy = 0.6,
        string? path = @"C:\library\track.flac")
        => new(new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            TrackUniqueHash = hash,
            Artist = $"Artist {hash}",
            Title = $"Title {hash}",
            MusicalKey = key,
            BPM = bpm,
            Energy = energy,
            // Status stays Pending so the VM doesn't kick off its background file-size probe.
            ResolvedFilePath = path ?? string.Empty,
        });

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }

    private static void StartFlowFrom(PlayerViewModel sut, PlaylistTrackViewModel current, int index = 0)
    {
        SetField(sut, "_isFlowMode", true);
        SetField(sut, "_currentQueueIndex", index);
        SetField(sut, "_currentTrack", current);
    }

    [Fact]
    public void PlanFlowPath_PublishesThePlanAndItsReason()
    {
        var current = Track("current", "8A", 128, 0.60);
        var next = Track("next", "8A", 128, 0.62);
        var far = Track("far", "2A", 174, 0.95);

        var sut = CreateSut(out var service, current, next, far);
        service.Activate();
        StartFlowFrom(sut, current);

        sut.PlanFlowPath();

        Assert.True(sut.IsFlowMode);
        Assert.True(sut.FlowModeState.HasPath);
        Assert.Equal("next", sut.FlowModeState.NextStep!.Hash);
        Assert.Contains("Flow next:", sut.FlowModeState.NextTrackReason);
        Assert.Equal(2, sut.FlowModeState.EvaluatedCandidates);
    }

    [Fact]
    public void PlanFlowPath_SkipsThePlayingRowAndRowsWithNoFile()
    {
        var current = Track("current");
        var noFile = Track("ghost", path: null);
        var usable = Track("usable", "9A", 128, 0.60);

        var sut = CreateSut(out var service, current, noFile, usable);
        service.Activate();
        StartFlowFrom(sut, current);

        sut.PlanFlowPath();

        var step = Assert.Single(sut.FlowModeState.FlowPath);
        Assert.Equal("usable", step.Hash);
    }

    [Fact]
    public void PlanFlowPath_WhenFlowIsOff_LeavesTheStateAlone()
    {
        var current = Track("current");
        var other = Track("other");

        var sut = CreateSut(out _, current, other);
        SetField(sut, "_isFlowMode", false);
        SetField(sut, "_currentQueueIndex", 0);
        SetField(sut, "_currentTrack", current);

        sut.PlanFlowPath();

        Assert.False(sut.FlowModeState.IsActive);
        Assert.Empty(sut.FlowModeState.FlowPath);
    }

    [Fact]
    public void PlanFlowPath_WithNothingElseQueued_SurfacesAReason()
    {
        var current = Track("current");

        var sut = CreateSut(out var service, current);
        service.Activate();
        StartFlowFrom(sut, current);

        sut.PlanFlowPath();

        Assert.False(sut.FlowModeState.HasPath);
        Assert.False(string.IsNullOrWhiteSpace(sut.FlowModeState.StatusMessage));
        // The line under the visualizer carries the explanation instead of going blank.
        Assert.Equal(sut.FlowModeState.StatusMessage, sut.FlowModeState.NextTrackReason);
    }

    [Fact]
    public void SelectingAPreset_ReplansWithTheNewWeighting()
    {
        var current = Track("current", "8A", 128, 0.50);
        var keyed = Track("keyed", "9A", 128, 0.95);
        var steady = Track("steady", "4A", 200, 0.50);

        var sut = CreateSut(out var service, current, keyed, steady);
        service.Activate();
        StartFlowFrom(sut, current);

        sut.PlanFlowPath();
        var smoothPick = sut.FlowModeState.NextStep!.Hash;

        sut.SelectedFlowPreset = FlowPresetOption.For(VibePreset.ClubBanger);

        Assert.Equal("keyed", smoothPick);
        Assert.Equal("steady", sut.FlowModeState.NextStep!.Hash);
        Assert.Equal(VibePreset.ClubBanger, sut.FlowModeState.ActivePreset);
    }
}
