using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.ViewModels.Setup;
using Xunit;

namespace SLSKDONET.Tests.Setup;

public class QuickTourViewModelTests
{
    [Fact]
    public void CoversTheTenMainAreas()
    {
        Assert.Equal(10, QuickTourViewModel.AllSteps.Count);
        Assert.Equal(new[] { "Search", "Downloads", "Import", "Library", "Analysis", "Cue Forge", "Flow Builder", "Edit Lab", "Workstation", "Settings" },
            QuickTourViewModel.AllSteps.Select(s => s.Title));
    }

    [Fact]
    public void AllVisible_WalksTenSteps_AndFinishMarksCompleted()
    {
        var state = new FakeSetupState();
        var tour = new QuickTourViewModel(state, NullLogger.Instance);
        Assert.True(tour.Start(_ => true));
        Assert.Equal("1 / 10", tour.PositionText);
        Assert.Equal("NavSearch", tour.CurrentTarget);

        for (var i = 0; i < 9; i++) tour.Next();
        Assert.Equal("10 / 10", tour.PositionText);
        Assert.Equal("Finish", tour.NextLabel);
        tour.Back();
        Assert.Equal("9 / 10", tour.PositionText);
        tour.Next();
        tour.Next();

        Assert.False(tour.IsOpen);
        Assert.True(state.IsTourCompleted);
    }

    [Fact]
    public void HiddenTargets_AreSkipped_AndFallbackTargetIsUsed()
    {
        var tour = new QuickTourViewModel(new FakeSetupState(), NullLogger.Instance);
        // Collapsed EXPERIMENTAL group and no sidebar Settings: only the top-bar gear is visible for Settings.
        var hidden = new[] { "NavCueForge", "NavEditLab", "NavFlowBuilder", "NavWorkstation", "NavSettings" };
        Assert.True(tour.Start(name => !hidden.Contains(name)));
        Assert.Equal(6, tour.Count);
        Assert.Equal("1 / 6", tour.PositionText);
        for (var i = 0; i < 5; i++) tour.Next();
        Assert.Equal("Settings", tour.Title);
        Assert.Equal("TopBarSettings", tour.CurrentTarget);
    }

    [Fact]
    public void NothingVisible_DoesNotStart_AndExplains()
    {
        var notifications = new FakeNotifications();
        var state = new FakeSetupState();
        var tour = new QuickTourViewModel(state, NullLogger.Instance, notifications);
        Assert.False(tour.Start(_ => false));
        Assert.False(tour.IsOpen);
        Assert.False(state.IsTourCompleted);
        Assert.Single(notifications.Shown);
    }

    [Fact]
    public void Skip_ClosesAndRemembers()
    {
        var state = new FakeSetupState();
        var tour = new QuickTourViewModel(state, NullLogger.Instance);
        tour.Start(_ => true);
        tour.Next();
        tour.SkipCommand.Execute(null);
        Assert.False(tour.IsOpen);
        Assert.Null(tour.CurrentTarget);
        Assert.True(state.IsTourCompleted);
    }

    [Fact]
    public void Progress_TracksTheCurrentStep()
    {
        var tour = new QuickTourViewModel(new FakeSetupState(), NullLogger.Instance);
        Assert.Equal(0.0, tour.Progress); // nothing started yet
        tour.Start(_ => true);
        Assert.Equal(0.1, tour.Progress, 3); // 1 / 10
        tour.Next();
        Assert.Equal(0.2, tour.Progress, 3); // 2 / 10
        for (var i = 0; i < 8; i++) tour.Next();
        Assert.Equal(1.0, tour.Progress, 3); // 10 / 10
    }

    [Fact]
    public void Back_AtFirstStep_IsNoOp()
    {
        var tour = new QuickTourViewModel(new FakeSetupState(), NullLogger.Instance);
        tour.Start(_ => true);
        tour.Back();
        Assert.Equal(0, tour.Index);
        Assert.Equal("NavSearch", tour.CurrentTarget);
    }

    [Fact]
    public void Close_IsIdempotent()
    {
        var state = new FakeSetupState();
        var tour = new QuickTourViewModel(state, NullLogger.Instance);
        tour.Start(_ => true);
        tour.Close(completed: false);
        Assert.False(tour.IsOpen);

        // A second close must not throw and must leave the tour closed.
        tour.Close(completed: true);
        Assert.False(tour.IsOpen);
        Assert.True(state.IsTourCompleted);
    }
}
