using System;
using System.IO;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// The fullscreen player fades its chrome away after a few seconds of inactivity. That only works if
/// "inactivity" is measured from real mouse movement: Avalonia re-raises PointerMoved when the element
/// under a stationary cursor changes (the seek thumb slides with every position tick), which made the
/// chrome pop back ~2–3 s after fading, then fade again, on a loop.
/// </summary>
public class FullscreenPlayerIdleGuardTests
{
    private static string CodeBehind() => File.ReadAllText(
        Path.Combine(RepoGuard.Root, "Views", "Avalonia", "ExpandedPlayerView.axaml.cs"));

    [Fact]
    public void Chrome_WakesOnlyOnRealPointerMovement()
    {
        var code = CodeBehind();

        Assert.Contains("PointerMoveEpsilon", code);
        Assert.Contains("private void OnPointerMoved", code);
        Assert.Contains("AddHandler(PointerMovedEvent, OnPointerMoved", code);

        // The old shape — wake on every PointerMoved regardless of distance — is the bug.
        Assert.DoesNotContain("AddHandler(PointerMovedEvent, (_, _) => WakeChrome()", code);
    }

    [Fact]
    public void Chrome_NeverFadesWhilePaused()
    {
        var code = CodeBehind();

        Assert.Contains("if (_vm?.IsPlaying == true) SetIdle(true);", code);
    }

    [Fact]
    public void Fading_IsIdempotent()
    {
        var code = CodeBehind();

        // Re-applying the same class (or cursor) on every wake caused needless transition restarts.
        Assert.Contains("panel.Classes.Contains(\"idle\") == idle", code);
        Assert.Contains("if (!Equals(Cursor, cursor)) Cursor = cursor;", code);
    }
}
