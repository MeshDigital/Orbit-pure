using System;
using System.Collections.Generic;
using SLSKDONET.Models.Entertainment;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Entertainment;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SLSKDONET.Tests.Entertainment;

/// <summary>
/// The Flow Mode runtime: state transitions, what the panel is told to show, and the guarantee that
/// an inactive/empty Flow reports a reason instead of leaving the UI blank.
/// </summary>
public class FlowModeServiceTests
{
    private static FlowModeService Create() => new(NullLogger<FlowModeService>.Instance);

    private static FlowCandidate C(string hash, double bpm = 128, string key = "8A", double energy = 0.6) =>
        new() { Hash = hash, Artist = "Artist", Title = hash, Bpm = bpm, CamelotKey = key, Energy = energy };

    private static readonly FlowCandidate Current = C("current");
    private static readonly FlowCandidate[] Pool = { C("a"), C("b"), C("c") };

    [Fact]
    public void Inactive_DoesNotPlan()
    {
        var service = Create();

        service.RebuildFlowPath(Current, Pool);

        Assert.False(service.State.IsActive);
        Assert.Equal(FlowPlanStatus.Off, service.State.Status);
        Assert.False(service.State.HasPath);
    }

    [Fact]
    public void Activated_Rebuild_PublishesPathAndTheOneLineReason()
    {
        var service = Create();
        service.Activate();

        service.RebuildFlowPath(Current, Pool);

        var state = service.State;
        Assert.True(state.IsActive);
        Assert.Equal(FlowPlanStatus.Ready, state.Status);
        Assert.True(state.HasPath);
        Assert.NotNull(state.NextStep);
        Assert.Equal(3, state.EvaluatedCandidates);
        Assert.Equal(3, state.AnalysedCandidates);
        Assert.Equal("Artist — current", state.PlannedFromName);
        Assert.Contains("Flow next:", state.NextTrackReason);
        Assert.InRange(state.CrossfadeDurationSeconds, 2.0, 12.0);
        Assert.NotNull(state.PlannedAt);
    }

    [Fact]
    public void EmptyQueue_LeavesAnExplanation_NotABlankPanel()
    {
        var service = Create();
        service.Activate();

        service.RebuildFlowPath(Current, Array.Empty<FlowCandidate>());

        var state = service.State;
        Assert.Equal(FlowPlanStatus.NoCandidates, state.Status);
        Assert.True(state.IsBlocked);
        Assert.False(state.HasPath);
        Assert.False(string.IsNullOrWhiteSpace(state.StatusMessage));
        Assert.Equal(state.StatusMessage, state.NextTrackReason);
    }

    [Fact]
    public void NoCurrentTrack_ReportsNoTrack()
    {
        var service = Create();
        service.Activate();

        service.RebuildFlowPath(null, Pool);

        Assert.Equal(FlowPlanStatus.NoTrack, service.State.Status);
    }

    [Fact]
    public void Deactivate_ClearsThePathButRemembersTheSetup()
    {
        var service = Create();
        service.Activate(VibePreset.GenreBender, 0.5);
        service.RebuildFlowPath(Current, Pool);

        service.Deactivate();

        var state = service.State;
        Assert.False(state.IsActive);
        Assert.False(state.HasPath);
        Assert.Equal(FlowPlanStatus.Off, state.Status);
        Assert.Equal(string.Empty, state.NextTrackReason);
        Assert.Equal(VibePreset.GenreBender, state.ActivePreset);
        Assert.Equal(0.5, state.EnergyNudge, 3);
    }

    [Fact]
    public void Toggle_RoundTripsWithoutLosingPreset()
    {
        var service = Create();
        service.Activate(VibePreset.ClubBanger);
        service.Toggle();

        Assert.False(service.State.IsActive);

        service.Toggle();

        Assert.True(service.State.IsActive);
        Assert.Equal(VibePreset.ClubBanger, service.State.ActivePreset);
    }

    [Fact]
    public void StateChanged_FiresForEveryTransition()
    {
        var service = Create();
        var snapshots = new List<FlowModeState>();
        service.StateChanged += (_, state) => snapshots.Add(state);

        service.Activate();
        service.SetPreset(VibePreset.RadioPerfect);
        service.SetEnergyNudge(0.25);
        service.RebuildFlowPath(Current, Pool);
        service.Deactivate();

        Assert.Equal(5, snapshots.Count);
        Assert.IsType<FlowModeState>(snapshots[^1]);
        Assert.False(snapshots[^1].IsActive);
    }

    [Fact]
    public void EnergyNudge_IsClamped()
    {
        var service = Create();
        service.Activate();

        service.SetEnergyNudge(4.0);
        Assert.Equal(1.0, service.State.EnergyNudge, 3);

        service.SetEnergyNudge(-4.0);
        Assert.Equal(-1.0, service.State.EnergyNudge, 3);
    }

    [Fact]
    public void Preset_DrivesTheScoringWeights()
    {
        // 4A vs 8A is a key clash; a very energetic track should only win when energy outranks harmony.
        var current = C("current", 128, "8A", 0.50);
        var pool = new[]
        {
            C("harmonious", 128, "9A", 0.95),
            C("steady", 200, "4A", 0.50),
        };

        var service = Create();
        service.Activate(VibePreset.SilkySmooth);
        service.RebuildFlowPath(current, pool);
        var smooth = service.State.NextStep!.Hash;

        service.SetPreset(VibePreset.ClubBanger);
        service.RebuildFlowPath(current, pool);
        var club = service.State.NextStep!.Hash;

        Assert.Equal("harmonious", smooth);
        Assert.Equal("steady", club);
    }
}
