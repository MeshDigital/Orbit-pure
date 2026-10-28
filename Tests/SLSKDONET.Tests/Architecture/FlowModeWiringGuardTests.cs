using System;
using System.IO;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Ratchet against Flow Mode silently reverting to a toggle that does nothing. Flow was previously a
/// fully-built engine (<c>IFlowModeService</c>) with no caller, no plan, and a panel that showed only
/// "Turn on Flow to plan the next tracks automatically." — the exact class of bug CLAUDE.md warns
/// about ("built but never wired").
/// </summary>
public class FlowModeWiringGuardTests
{
    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoGuard.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Player_PlansFlowOnEveryTrackChange()
    {
        var vm = Read("ViewModels/PlayerViewModel.cs");

        var start = vm.IndexOf("private void SetNowPlayingState(", StringComparison.Ordinal);
        var end = vm.IndexOf("/// <summary>Queue index the engine currently has preloaded", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Could not isolate SetNowPlayingState.");

        var method = vm[start..end];
        Assert.Contains("PlanFlowPath();", method);
        Assert.Contains("RememberFlowHistory(track);", method);
    }

    [Fact]
    public void Player_ActuallyUsesTheFlowPlanToChooseTheNextTrack()
    {
        var vm = Read("ViewModels/PlayerViewModel.cs");

        Assert.Contains("_flowModeService.RebuildFlowPath(", vm);
        // Preload (PeekNextIndex) and the manual Skip button (PlayNextTrack) must both follow the plan.
        Assert.Contains("if (PeekFlowNextIndex() is int flowIndex) return flowIndex;", vm);
        Assert.Contains("else if (PeekFlowNextIndex() is int flowNext)", vm);
    }

    [Fact]
    public void NowPlaying_ShowsNextTrackAndTheReasons_NotJustTheToggle()
    {
        var xaml = Read("Views/Avalonia/NowPlayingPage.axaml");

        Assert.Contains("{Binding FlowModeState.NextStep.Title}", xaml);
        Assert.Contains("{Binding FlowModeState.NextStep.MetaLine}", xaml);
        Assert.Contains("{Binding FlowModeState.NextStep.KeyRowText}", xaml);
        Assert.Contains("{Binding FlowModeState.NextStep.TempoRowText}", xaml);
        Assert.Contains("{Binding FlowModeState.NextStep.EnergyRowText}", xaml);
        Assert.Contains("{Binding FlowModeState.NextStep.VibeRowText}", xaml);
        Assert.Contains("ItemsSource=\"{Binding FlowModeState.LaterSteps}\"", xaml);
        Assert.Contains("{Binding FlowModeState.StatusMessage}", xaml);
        Assert.Contains("Command=\"{Binding ToggleFlowModeCommand}\"", xaml);
        Assert.Contains("{Binding SelectedFlowPreset, Mode=TwoWay}", xaml);
        Assert.Contains("{Binding FlowEnergyNudgePercent, Mode=TwoWay}", xaml);
    }

    [Fact]
    public void FullscreenOverlay_NamesTheFlowPick()
    {
        var xaml = Read("Views/Avalonia/ExpandedPlayerView.axaml");

        Assert.Contains("{Binding FlowModeState.NextTrackReason}", xaml);
        Assert.Contains("IsVisible=\"{Binding IsFlowMode}\"", xaml);
    }

    [Fact]
    public void FlowService_StaysDatabaseFree_SoAPlanIsReadyBeforeThePreload()
    {
        var service = Read("Services/Entertainment/FlowModeService.cs");

        Assert.DoesNotContain("IDbContextFactory", service);
        Assert.DoesNotContain("async Task", service);
        Assert.DoesNotContain("RebuildFlowPathAsync", service);
    }
}
