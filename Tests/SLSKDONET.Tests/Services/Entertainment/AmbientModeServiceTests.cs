using System;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Entertainment;
using Xunit;

namespace SLSKDONET.Tests.Services.Entertainment;

/// <summary>
/// Regression: the fullscreen visualizer showed visuals for a few seconds and then fell back to
/// Ambient, because nothing ever reported user activity and the idle timer always fired.
/// </summary>
public class AmbientModeServiceTests
{
    private static AmbientModeService Create() => new(NullLogger<AmbientModeService>.Instance);

    [Fact]
    public void ChoosingVisuals_ResetsIdleClock_SoAmbientDoesNotComeStraightBack()
    {
        using var svc = Create();
        svc.Activate();
        svc.Deactivate(); // user picks visuals

        svc.EvaluateTimeouts(DateTime.UtcNow.AddSeconds(10));

        Assert.False(svc.IsActive);
    }

    [Fact]
    public void IdleTimeout_DoesNotSwitchToAmbient_WhileMusicIsPlaying()
    {
        using var svc = Create();
        svc.NotifyPlaybackState(true);

        svc.EvaluateTimeouts(DateTime.UtcNow.AddSeconds(svc.Config.IdleTimeoutSeconds + 60));

        Assert.False(svc.IsActive);
    }

    [Fact]
    public void IdleTimeout_SwitchesToAmbient_WhenNothingIsPlaying()
    {
        using var svc = Create();

        svc.EvaluateTimeouts(DateTime.UtcNow.AddSeconds(svc.Config.IdleTimeoutSeconds + 1));

        Assert.True(svc.IsActive);
    }

    [Fact]
    public void UserActivity_ExitsAutomaticAmbient()
    {
        using var svc = Create();
        svc.EvaluateTimeouts(DateTime.UtcNow.AddSeconds(svc.Config.IdleTimeoutSeconds + 1));
        Assert.True(svc.IsActive);

        svc.NotifyUserActivity();

        Assert.False(svc.IsActive);
    }

    [Fact]
    public void UserActivity_KeepsManuallyChosenAmbient()
    {
        using var svc = Create();
        svc.Toggle(); // user turns Ambient on

        svc.NotifyUserActivity();

        Assert.True(svc.IsActive);
    }

    [Fact]
    public void RecentActivity_PreventsIdleAmbient()
    {
        using var svc = Create();
        var later = DateTime.UtcNow.AddSeconds(svc.Config.IdleTimeoutSeconds - 5);
        svc.NotifyUserActivity();

        svc.EvaluateTimeouts(later);

        Assert.False(svc.IsActive);
    }
}
